using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Piggy;
using Khela.Common.Store;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.Game.Net;
using UnityEngine;
using UnityEngine.Purchasing;

namespace PlayCard.Store
{
    /// <summary>
    /// Unity IAP (5.x <c>StoreController</c>) behind the SAME public surface as WGWB's <c>IAPService</c> — state, queries,
    /// <see cref="TryPurchase"/>, restore, the four events — so buttons and habits port 1:1. The one difference that
    /// matters: <b>this client never grants anything.</b> A pending order is handed to the server
    /// (<c>POST /api/store/redeem</c>), which verifies the receipt with the store and credits the wallet; the order is
    /// confirmed with the store ONLY after the server says Granted / AlreadyGranted / Invalid. A transient failure leaves
    /// the order pending — Unity re-delivers pending orders on every launch and the server is idempotent on the store
    /// transaction, so a crash between grant and confirm can never lose (or double-pay) a purchase. There is no local
    /// "processed transactions" list: the server IS the idempotency (docs/IAP_SPEC.md §5.2, §8).
    ///
    /// Products come from the server catalog (<see cref="StoreCatalog"/>): our product id is the Unity product id, the
    /// catalog's store-product id for THIS platform is the store-specific id. Prices are the store's (localized).
    ///
    /// Lives on a persistent GameObject. Place one in the boot scene to set the knobs in the inspector; if none exists,
    /// it bootstraps itself with the defaults (like <c>KhelaAuthService</c>).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IapService : MonoBehaviour
    {
        public enum InitializationState
        {
            Uninitialized = 0,
            WaitingForCatalog = 1,
            InitializingStore = 2,
            FetchingProducts = 3,
            Ready = 4,
            Disconnected = 5,
            Failed = 6,
            Unsupported = 7,   // WebGL / a platform Unity IAP can't run on
        }

        public enum PurchaseStatus
        {
            Success = 0,          // the server granted (or had already granted) — balances are updated
            Failed = 1,           // the store flow failed (cancelled, declined, unavailable…)
            Deferred = 2,         // parental approval / deferred payment at the store
            NotReady = 3,
            ProductNotFound = 4,
            ProductUnavailable = 5,
            AlreadyProcessing = 6,
            NotEligible = 7,      // the server's intent check said no (limit, level, piggy not full…) — Message says why
            Rejected = 8,         // the server verified the receipt and REJECTED it (the order is finished; nothing paid)
            Pending = 9,          // the server could not complete it yet (payment pending / transient) — it will be retried
        }

        public sealed class PurchaseResult
        {
            public string productId = string.Empty;
            public string storeProductId = string.Empty;
            public string transactionId = string.Empty;
            public PurchaseStatus status = PurchaseStatus.Failed;
            public PurchaseFailureReason failureReason = PurchaseFailureReason.Unknown;
            public string message = string.Empty;
            /// <summary>The server's answer for Success / Rejected / Pending — grants, balances, piggy/pass payloads.</summary>
            public StoreRedeemResultData redeem;
        }

        public static IapService Instance { get; private set; }

        [Header("Lifecycle")]
        [SerializeField] private bool dontDestroyOnLoad = true;
        [Tooltip("Start Unity IAP as soon as the player is signed in (AccountManager.IsReady). Off = call Initialize() yourself.")]
        [SerializeField] private bool initializeOnLogin = true;
        [Tooltip("Seconds to wait for sign-in before giving up the automatic start (Initialize() can still be called later).")]
        [SerializeField] private float signInWaitSeconds = 120f;

        [Header("Store")]
        [Tooltip("After the products are fetched, ask the store for existing purchases so pending orders are re-driven.")]
        [SerializeField] private bool fetchExistingPurchasesAfterProducts = true;
        [Tooltip("How many times to re-ask the store for the product list before declaring Failed.")]
        [SerializeField] private int productFetchAttempts = 3;
        [Tooltip("Reconnect automatically when the store disconnects / the app regains focus.")]
        [SerializeField] private bool reconnectOnFocus = true;

        [Header("Redeem (server)")]
        [Tooltip("Redeem attempts per pending order before leaving it for the next launch / FetchPurchases.")]
        [SerializeField] private int redeemAttempts = 3;
        [SerializeField] private float redeemBackoffSeconds = 2f;
        [SerializeField] private float redeemBackoffMaxSeconds = 30f;

        [Header("Diagnostics")]
        [SerializeField] private bool verboseLog = false;

        private StoreController storeController;
        private InitializationState initializationState = InitializationState.Uninitialized;
        private string lastStatusMessage = string.Empty;
        private bool initializeStarted;
        private bool hasCompletedInitialProductFetch;
        private int productFetchTries;
        private int connectTries;

        private readonly Dictionary<string, Product> fetchedProductsByProductId = new Dictionary<string, Product>(StringComparer.Ordinal);
        private readonly HashSet<string> processingProductIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> redeemingTransactionIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> lastKnownPriceByProductId = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> lastKnownTitleByProductId = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> lastKnownDescriptionByProductId = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> intentIdByProductId = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<PendingOrder> ordersWaitingForAuth = new List<PendingOrder>();

        public event Action<InitializationState, string> OnInitializationStateChanged;
        public event Action OnCatalogUpdated;
        public event Action<string, bool> OnProcessingStateChanged;
        public event Action<PurchaseResult> OnPurchaseCompleted;

        public InitializationState State => initializationState;
        public string LastStatusMessage => lastStatusMessage;
        public bool IsReady => initializationState == InitializationState.Ready;
        public StorePlatform Platform => StorePlatformResolver.Current;

        // ------------------------------------------------------------------ lifecycle

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
#if !UNITY_SERVER
            if (Instance != null) return;
            var go = new GameObject("[IapService]");
            go.AddComponent<IapService>();
#endif
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // A scene-placed instance (with inspector knobs) should win over the bootstrapped default: if WE are the
                // bootstrapped one and a placed one arrives, the placed one replaces us; otherwise the newcomer dies.
                if (Instance.gameObject.name == "[IapService]" && gameObject.name != "[IapService]")
                {
                    Destroy(Instance.gameObject);
                }
                else
                {
                    Destroy(gameObject);
                    return;
                }
            }
            Instance = this;
            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
            if (initializeOnLogin) Initialize();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            UnsubscribeStoreCallbacks();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus || !reconnectOnFocus || storeController == null) return;
            if (initializationState == InitializationState.Disconnected || initializationState == InitializationState.Failed)
            {
                connectTries = 0;
                ConnectStoreAsync();
            }
        }

        /// <summary>Start Unity IAP (idempotent). Waits for sign-in, fetches the server catalog, connects, fetches products and pending orders.</summary>
        public void Initialize()
        {
            if (initializeStarted) return;
            initializeStarted = true;
            StartCoroutine(InitializeWhenReady());
        }

        private IEnumerator InitializeWhenReady()
        {
            if (!StorePlatformResolver.UnityIapSupported)
            {
                SetState(InitializationState.Unsupported, $"Unity IAP is not available on {StorePlatformResolver.Current}.");
                yield break;
            }

            SetState(InitializationState.WaitingForCatalog, "Waiting for sign-in.");
            float waited = 0f;
            while (AccountManager.Instance != null && !AccountManager.Instance.IsReady && waited < signInWaitSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            // The server catalog names the products (and their store ids for this platform). Use the disk cache if the
            // network is slow so the store can connect immediately; the real list replaces it on the next refresh.
            StoreCatalog.Instance.LoadCached();
            var refresh = StoreCatalog.Instance.RefreshAsync(force: true);
            while (!refresh.IsCompleted) yield return null;
            if (!StoreCatalog.Instance.Loaded || StoreCatalog.Instance.Products.Count == 0)
            {
                SetState(InitializationState.Failed, "No store catalog available.");
                yield break;
            }

            CreateStoreController();
            SubscribeStoreCallbacks();
            storeController.ProcessPendingOrdersOnPurchasesFetched(false);   // we drive pending orders ourselves (server redeem first)
            ConnectStoreAsync();
        }

        private void CreateStoreController()
        {
            if (storeController != null) return;
            storeController = UnityIAPServices.StoreController();
        }

        private async void ConnectStoreAsync()
        {
            if (storeController == null) return;
            try
            {
                connectTries++;
                SetState(InitializationState.InitializingStore, "Connecting to the store.");
                await storeController.Connect();
            }
            catch (Exception exception)
            {
                SetState(InitializationState.Failed, exception.Message);
            }
        }

        private void SubscribeStoreCallbacks()
        {
            if (storeController == null) return;
            storeController.OnStoreConnected += HandleStoreConnected;
            storeController.OnStoreDisconnected += HandleStoreDisconnected;
            storeController.OnProductsFetched += HandleProductsFetched;
            storeController.OnProductsFetchFailed += HandleProductsFetchFailed;
            storeController.OnPurchasesFetched += HandlePurchasesFetched;
            storeController.OnPurchasesFetchFailed += HandlePurchasesFetchFailed;
            storeController.OnPurchasePending += HandlePurchasePending;
            storeController.OnPurchaseConfirmed += HandlePurchaseConfirmed;
            storeController.OnPurchaseFailed += HandlePurchaseFailed;
            storeController.OnPurchaseDeferred += HandlePurchaseDeferred;
        }

        private void UnsubscribeStoreCallbacks()
        {
            if (storeController == null) return;
            storeController.OnStoreConnected -= HandleStoreConnected;
            storeController.OnStoreDisconnected -= HandleStoreDisconnected;
            storeController.OnProductsFetched -= HandleProductsFetched;
            storeController.OnProductsFetchFailed -= HandleProductsFetchFailed;
            storeController.OnPurchasesFetched -= HandlePurchasesFetched;
            storeController.OnPurchasesFetchFailed -= HandlePurchasesFetchFailed;
            storeController.OnPurchasePending -= HandlePurchasePending;
            storeController.OnPurchaseConfirmed -= HandlePurchaseConfirmed;
            storeController.OnPurchaseFailed -= HandlePurchaseFailed;
            storeController.OnPurchaseDeferred -= HandlePurchaseDeferred;
        }

        // ------------------------------------------------------------------ store connection + products

        private void HandleStoreConnected()
        {
            connectTries = 0;
            productFetchTries = 0;
            FetchCatalogProducts();
        }

        private void HandleStoreDisconnected(StoreConnectionFailureDescription description)
        {
            SetState(InitializationState.Disconnected, description != null ? description.message : "Store disconnected.");
            if (reconnectOnFocus && connectTries < 3) StartCoroutine(ReconnectAfter(Mathf.Min(30f, 2f * connectTries + 2f)));
        }

        private IEnumerator ReconnectAfter(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (initializationState == InitializationState.Disconnected) ConnectStoreAsync();
        }

        private void FetchCatalogProducts()
        {
            var definitions = BuildProductDefinitions();
            if (definitions.Count == 0)
            {
                SetState(InitializationState.Failed, "No products for this platform in the catalog.");
                return;
            }
            productFetchTries++;
            SetState(InitializationState.FetchingProducts, $"Fetching {definitions.Count} store products.");
            storeController.FetchProductsWithNoRetries(definitions);
        }

        private List<ProductDefinition> BuildProductDefinitions()
        {
            var list = new List<ProductDefinition>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenStoreIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in StoreCatalog.Instance.Products)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.StoreProductId)) continue;
                if (!seenIds.Add(p.Id)) { Debug.LogWarning($"[IapService] duplicate product id skipped: {p.Id}"); continue; }
                if (!seenStoreIds.Add(p.StoreProductId)) { Debug.LogWarning($"[IapService] duplicate store id skipped: {p.StoreProductId}"); continue; }
                list.Add(new ProductDefinition(p.Id, p.StoreProductId, ToPurchasingProductType(p.ProductType), true));
            }
            return list;
        }

        private static UnityEngine.Purchasing.ProductType ToPurchasingProductType(StoreProductType type)
        {
            switch (type)
            {
                case StoreProductType.NonConsumable: return UnityEngine.Purchasing.ProductType.NonConsumable;
                case StoreProductType.Subscription: return UnityEngine.Purchasing.ProductType.Subscription;
                default: return UnityEngine.Purchasing.ProductType.Consumable;
            }
        }

        private void HandleProductsFetched(List<Product> products)
        {
            hasCompletedInitialProductFetch = true;
            RefreshFetchedProductLookup(products);
            SetState(InitializationState.Ready, $"Fetched {fetchedProductsByProductId.Count} products.");
            OnCatalogUpdated?.Invoke();
            if (fetchExistingPurchasesAfterProducts) storeController.FetchPurchases();
        }

        private void HandleProductsFetchFailed(ProductFetchFailed failure)
        {
            var message = failure != null ? failure.FailureReason : "Product fetch failed.";
            if (productFetchTries < Mathf.Max(1, productFetchAttempts))
            {
                Log($"product fetch failed ({message}); retrying {productFetchTries}/{productFetchAttempts}");
                StartCoroutine(RefetchAfter(2f * productFetchTries));
                return;
            }
            hasCompletedInitialProductFetch = true;
            RefreshFetchedProductLookup(storeController != null ? storeController.GetProducts().ToList() : null);
            SetState(fetchedProductsByProductId.Count > 0 ? InitializationState.Ready : InitializationState.Failed, message);
            OnCatalogUpdated?.Invoke();
        }

        private IEnumerator RefetchAfter(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (storeController != null) FetchCatalogProducts();
        }

        private void RefreshFetchedProductLookup(List<Product> products)
        {
            if (products == null || products.Count == 0) return;
            foreach (var product in products)
            {
                if (product?.definition == null) continue;
                var id = product.definition.id;
                if (string.IsNullOrWhiteSpace(id)) continue;
                fetchedProductsByProductId[id] = product;
                if (product.metadata == null) continue;
                if (!string.IsNullOrWhiteSpace(product.metadata.localizedPriceString)) lastKnownPriceByProductId[id] = product.metadata.localizedPriceString;
                if (!string.IsNullOrWhiteSpace(product.metadata.localizedTitle)) lastKnownTitleByProductId[id] = product.metadata.localizedTitle;
                if (!string.IsNullOrWhiteSpace(product.metadata.localizedDescription)) lastKnownDescriptionByProductId[id] = product.metadata.localizedDescription;
            }
        }

        // ------------------------------------------------------------------ queries (WGWB surface)

        public bool HasFetchedProduct(string productId)
            => !string.IsNullOrWhiteSpace(productId) && fetchedProductsByProductId.ContainsKey(productId);

        public bool IsProcessing(string productId)
            => !string.IsNullOrWhiteSpace(productId) && processingProductIds.Contains(productId);

        /// <summary>Only the golden pass is an "owned" product (a live subscription); consumables are never owned.</summary>
        public bool IsOwned(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return false;
            if (!StoreCatalog.Instance.TryGet(productId, out var p) || p.Effect == null) return false;
            if (!string.Equals(p.Effect.Type, "GoldenPass", StringComparison.OrdinalIgnoreCase)) return false;
            return PlayCard.Pass.PassState.Instance.IsGolden;
        }

        public bool IsProductExplicitlyUnavailable(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return false;
            if (!hasCompletedInitialProductFetch || initializationState != InitializationState.Ready) return false;
            if (!fetchedProductsByProductId.TryGetValue(productId, out var product) || product == null) return true;
            return !product.availableToPurchase;
        }

        /// <summary>The server said this player can't buy it right now (limit, level, window…). Null = purchasable / unknown.</summary>
        public string IneligibilityReason(string productId)
            => StoreCatalog.Instance.TryGet(productId, out var p) && !p.Purchasable ? (p.Reason ?? "Not available") : null;

        public string GetLocalizedPriceString(string productId, string fallback = "")
            => Localized(productId, fallback, lastKnownPriceByProductId, m => m.localizedPriceString);

        public string GetLocalizedTitle(string productId, string fallback = "")
            => Localized(productId, fallback, lastKnownTitleByProductId, m => m.localizedTitle);

        public string GetLocalizedDescription(string productId, string fallback = "")
            => Localized(productId, fallback, lastKnownDescriptionByProductId, m => m.localizedDescription);

        private string Localized(string productId, string fallback, Dictionary<string, string> cache, Func<ProductMetadata, string> pick)
        {
            if (!string.IsNullOrWhiteSpace(productId) && fetchedProductsByProductId.TryGetValue(productId, out var product) && product?.metadata != null)
            {
                var value = pick(product.metadata);
                if (!string.IsNullOrWhiteSpace(value)) { cache[productId] = value; return value; }
            }
            return !string.IsNullOrWhiteSpace(productId) && cache.TryGetValue(productId, out var cached) && !string.IsNullOrWhiteSpace(cached) ? cached : fallback;
        }

        /// <summary>Ask the store for existing purchases again (re-drives pending orders through the server).</summary>
        public void RefreshExistingPurchases()
        {
            if (storeController == null || initializationState == InitializationState.Uninitialized || initializationState == InitializationState.WaitingForCatalog) return;
            storeController.FetchPurchases();
        }

        /// <summary>The store-mandated "Restore purchases" (Apple shows a sign-in sheet). Completed orders come back through OnPurchasesFetched.</summary>
        public void RestoreTransactions(Action<bool, string> onDone = null)
        {
            if (storeController == null) { onDone?.Invoke(false, "Store not ready."); return; }
            storeController.RestoreTransactions((success, error) =>
            {
                Log($"restore finished: success={success} error={error}");
                if (success) storeController.FetchPurchases();
                onDone?.Invoke(success, error);
            });
        }

        // ------------------------------------------------------------------ purchase

        /// <summary>
        /// Buy one of OUR products. Returns true when the purchase was accepted for processing (the result arrives on
        /// <see cref="OnPurchaseCompleted"/>); false when it was refused up front (and a result was already emitted).
        /// The server's intent check runs first: a product the player may not buy right now never opens the store sheet.
        /// </summary>
        public bool TryPurchase(string productId)
        {
            var blocked = ValidatePurchaseRequest(productId);
            if (blocked != null) { EmitPurchaseResult(blocked); return false; }
            if (!fetchedProductsByProductId.TryGetValue(productId, out var product) || product == null)
            {
                EmitPurchaseResult(CreateResult(productId, string.Empty, PurchaseStatus.ProductNotFound, PurchaseFailureReason.ProductUnavailable, "Fetched product not found."));
                return false;
            }
            AddProcessingProduct(productId);
            KhelaAnalytics.LogPurchaseStarted(productId);
            _ = PurchaseAfterIntentAsync(productId, product);
            return true;
        }

        /// <summary>Buy the piggy offer for the player's CURRENT tier — the bridge from <c>PiggyPanel.BreakRequested</c>.</summary>
        public bool TryPurchasePiggy(PiggyBreakOption option)
        {
            int tier = PlayCard.Piggy.PiggyState.Instance.Current != null ? PlayCard.Piggy.PiggyState.Instance.Current.Tier : 1;
            return TryPurchase(StoreCatalog.Instance.PiggyProductId(tier, option));
        }

        private async Task PurchaseAfterIntentAsync(string productId, Product product)
        {
            try
            {
                var intent = await BlackjackRestClient.Instance.StoreIntentAsync(productId, StorePlatformResolver.Current);
                if (this == null) return;
                if (!intent.Ok || intent.Value == null || !intent.Value.Ok)
                {
                    var why = intent.Value?.Error ?? intent.Error ?? "Not available right now.";
                    RemoveProcessingProduct(productId);
                    EmitPurchaseResult(CreateResult(productId, product.definition.storeSpecificId, PurchaseStatus.NotEligible, PurchaseFailureReason.ProductUnavailable, why));
                    return;
                }
                if (!string.IsNullOrEmpty(intent.Value.IntentId)) intentIdByProductId[productId] = intent.Value.IntentId;
                BindAccountToStore();
                storeController.PurchaseProduct(product);
            }
            catch (Exception exception)
            {
                RemoveProcessingProduct(productId);
                EmitPurchaseResult(CreateResult(productId, product?.definition?.storeSpecificId, PurchaseStatus.Failed, PurchaseFailureReason.Unknown, exception.Message));
            }
        }

        /// <summary>
        /// Tell the store which of our accounts is buying (Google obfuscated account id = sha256 of the user id; Apple
        /// appAccountToken = the user id). A fraud SIGNAL on the server, never a gate. In Unity IAP 5.2.0 these live on the
        /// STORE extended services — <c>IGooglePlayStoreExtendedService.SetObfuscatedAccountId(string)</c> and the Apple
        /// store service's <c>SetAppAccountToken</c> — reached through <c>UnityIAPServices.Store()</c>, not through the purchase
        /// service's <c>.Google/.Apple</c> (those only carry upgrade/receipt helpers). Behind a define until the exact accessor is
        /// confirmed in the Editor against the installed package; the server treats a missing binding as "unknown", not as fraud.
        /// </summary>
        private void BindAccountToStore()
        {
#if KHELA_IAP_ACCOUNT_BINDING
            try
            {
                var userId = AccountManager.Instance != null ? AccountManager.Instance.UserId : null;
                if (string.IsNullOrEmpty(userId) || storeController == null) return;
                var store = UnityIAPServices.Store();
#if UNITY_ANDROID
                store?.Google?.SetObfuscatedAccountId(Sha256Hex(userId));
#elif UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX
                if (Guid.TryParse(userId, out var g)) store?.Apple?.SetAppAccountToken(g);
#endif
            }
            catch (Exception ex) { Debug.LogWarning($"[IapService] account binding failed: {ex.Message}"); }
#endif
        }

        private static string Sha256Hex(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private PurchaseResult ValidatePurchaseRequest(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId))
                return CreateResult(string.Empty, string.Empty, PurchaseStatus.ProductNotFound, PurchaseFailureReason.ProductUnavailable, "Missing product id.");
            if (initializationState != InitializationState.Ready)
                return CreateResult(productId, string.Empty, PurchaseStatus.NotReady, PurchaseFailureReason.PurchasingUnavailable, "Store not ready.");
            if (processingProductIds.Contains(productId))
                return CreateResult(productId, string.Empty, PurchaseStatus.AlreadyProcessing, PurchaseFailureReason.Unknown, "Purchase already in progress.");
            if (!StoreCatalog.Instance.TryGet(productId, out var catalogProduct) || catalogProduct == null)
                return CreateResult(productId, string.Empty, PurchaseStatus.ProductNotFound, PurchaseFailureReason.ProductUnavailable, "Unknown product.");
            if (IsOwned(productId))
                return CreateResult(productId, catalogProduct.StoreProductId, PurchaseStatus.ProductUnavailable, PurchaseFailureReason.DuplicateTransaction, "Already owned.");
            if (!fetchedProductsByProductId.TryGetValue(productId, out var product) || product == null || !product.availableToPurchase)
                return CreateResult(productId, catalogProduct.StoreProductId, PurchaseStatus.ProductUnavailable, PurchaseFailureReason.ProductUnavailable, "Product unavailable.");
            return null;
        }

        // ------------------------------------------------------------------ orders

        private void HandlePurchasesFetched(Orders existingOrders)
        {
            if (existingOrders == null) return;
            foreach (var order in existingOrders.PendingOrders) ProcessPendingOrder(order);
            // Confirmed orders need nothing: consumables were already granted server-side before we confirmed, and the
            // golden pass re-syncs from the server (PassState), so there is no client-held ownership to restore.
        }

        private void HandlePurchasesFetchFailed(PurchasesFetchFailureDescription failure)
        {
            Debug.LogWarning($"[IapService] existing purchases fetch failed: {failure?.Message}");
        }

        private void HandlePurchasePending(PendingOrder order) => ProcessPendingOrder(order);

        private void ProcessPendingOrder(PendingOrder order)
        {
            if (order == null) return;
            // Pending orders can arrive before sign-in is ready (OnPurchasesFetched fires right after Connect on a cold
            // start). Queue them; they are driven the moment the account is ready. Never drop a paid order.
            if (AccountManager.Instance != null && !AccountManager.Instance.IsReady)
            {
                if (!ordersWaitingForAuth.Contains(order))
                {
                    ordersWaitingForAuth.Add(order);
                    if (ordersWaitingForAuth.Count == 1) StartCoroutine(DrainWhenSignedIn());
                }
                return;
            }
            _ = RedeemOrderAsync(order);
        }

        private IEnumerator DrainWhenSignedIn()
        {
            float waited = 0f;
            while (AccountManager.Instance != null && !AccountManager.Instance.IsReady && waited < signInWaitSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            var batch = ordersWaitingForAuth.ToList();
            ordersWaitingForAuth.Clear();
            foreach (var order in batch) _ = RedeemOrderAsync(order);
        }

        /// <summary>
        /// THE client half of the money path: redeem the order on the server, then — and only then — confirm it with
        /// the store. Granted / AlreadyGranted / Invalid → confirm. Pending / transient / error → leave it pending (the
        /// store re-delivers it; the server is idempotent). Confirming early is the only way to lose a paid purchase.
        /// </summary>
        private async Task RedeemOrderAsync(PendingOrder order)
        {
            var items = order.CartOrdered.Items().ToList();
            var transactionId = order.Info != null ? order.Info.TransactionID : string.Empty;
            if (!string.IsNullOrEmpty(transactionId))
            {
                if (redeemingTransactionIds.Contains(transactionId)) return;   // one in-flight redeem per transaction
                redeemingTransactionIds.Add(transactionId);
            }
            try
            {
                bool confirm = true;
                foreach (var item in items)
                {
                    var product = item?.Product;
                    if (product?.definition == null) continue;
                    var productId = product.definition.id;
                    var storeProductId = product.definition.storeSpecificId;
                    var catalogProduct = StoreCatalog.Instance.TryGet(productId, out var cp) ? cp : StoreCatalog.Instance.ByStoreProductId(storeProductId);

                    var request = new RedeemPurchaseRequest
                    {
                        Platform = StorePlatformResolver.Current,
                        ProductId = catalogProduct?.Id ?? productId,
                        StoreProductId = storeProductId,
                        TransactionId = transactionId,
                        Receipt = order.Info != null ? order.Info.Receipt : null,
                        ClientPriceMicros = product.metadata != null ? (long?)decimal.ToInt64(decimal.Round(product.metadata.localizedPrice * 1_000_000m)) : null,
                        ClientPriceCurrency = product.metadata != null ? product.metadata.isoCurrencyCode : null,
                        ClientPurchaseId = intentIdByProductId.TryGetValue(productId, out var intentId) ? intentId : null,
                        ClientVersion = Application.version,
                    };
#if UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX
                    // StoreKit 2: the signed transaction is what the server verifies (the unified Receipt is the whole app receipt on Apple).
                    try { request.Jws = order.Info != null && order.Info.Apple != null ? order.Info.Apple.jwsRepresentation : null; }
                    catch (Exception ex) { Debug.LogWarning($"[IapService] jwsRepresentation unavailable: {ex.Message}"); }
#endif

                    var redeem = await RedeemWithRetriesAsync(request);
                    if (this == null) return;
                    intentIdByProductId.Remove(productId);

                    if (redeem == null)
                    {
                        // transport never answered: leave the order pending for the next FetchPurchases / launch
                        confirm = false;
                        RemoveProcessingProduct(productId);
                        EmitPurchaseResult(CreateResult(productId, storeProductId, PurchaseStatus.Pending, PurchaseFailureReason.Unknown, "Could not reach the server — the purchase will be completed shortly.", transactionId));
                        continue;
                    }

                    if (redeem.IsGranted)
                    {
                        RemoveProcessingProduct(productId);
                        var usd = catalogProduct != null ? (double)catalogProduct.UsdReference : 0d;
                        if (redeem.Status == StoreRedeemResultData.StatusGranted && !redeem.IsTest)
                            KhelaAnalytics.LogPurchaseCompleted(productId, "USD", usd);
                        EmitPurchaseResult(CreateResult(productId, storeProductId, PurchaseStatus.Success, PurchaseFailureReason.Unknown,
                            redeem.Status == StoreRedeemResultData.StatusAlreadyGranted ? "Purchase already delivered." : "Purchase delivered.", transactionId, redeem));
                    }
                    else if (redeem.Status == StoreRedeemResultData.StatusInvalid)
                    {
                        RemoveProcessingProduct(productId);
                        EmitPurchaseResult(CreateResult(productId, storeProductId, PurchaseStatus.Rejected, PurchaseFailureReason.SignatureInvalid, redeem.Error ?? "The purchase could not be verified.", transactionId, redeem));
                    }
                    else
                    {
                        // Pending / ProductUnavailable / PlatformDisabled / StoreDisabled / Error(transient): keep the order, retry later.
                        confirm = false;
                        RemoveProcessingProduct(productId);
                        EmitPurchaseResult(CreateResult(productId, storeProductId, PurchaseStatus.Pending, PurchaseFailureReason.Unknown, redeem.Error ?? "Purchase pending.", transactionId, redeem));
                    }
                }

                if (confirm)
                {
                    Log($"confirming order {transactionId}");
                    storeController.ConfirmPurchase(order);
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(transactionId)) redeemingTransactionIds.Remove(transactionId);
            }
        }

        /// <summary>Redeem with bounded backoff on transient failures. Null = never got a usable answer.</summary>
        private async Task<StoreRedeemResultData> RedeemWithRetriesAsync(RedeemPurchaseRequest request)
        {
            int attempts = Mathf.Max(1, redeemAttempts);
            float delay = Mathf.Max(0.5f, redeemBackoffSeconds);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                var result = await BlackjackRestClient.Instance.RedeemPurchaseAsync(request);
                if (result.Ok && result.Value != null)
                {
                    var value = result.Value;
                    bool retryable = !value.Ok && (value.Transient || value.Status == StoreRedeemResultData.StatusError);
                    if (!retryable || attempt == attempts) return value;
                }
                else
                {
                    Log($"redeem transport failure ({result.Status}: {result.Error}); attempt {attempt}/{attempts}");
                    if (attempt == attempts) return result.Value;   // may be null
                }
                await Task.Delay(TimeSpan.FromSeconds(delay));
                delay = Mathf.Min(redeemBackoffMaxSeconds, delay * 2.5f);
                if (this == null) return null;
            }
            return null;
        }

        private void HandlePurchaseConfirmed(Order order)
        {
            switch (order)
            {
                case FailedOrder failedOrder:
                    foreach (var item in failedOrder.CartOrdered.Items())
                    {
                        var product = item?.Product;
                        var productId = ResolveProductId(product);
                        RemoveProcessingProduct(productId);
                        Debug.LogWarning($"[IapService] confirm failed for {productId}: {failedOrder.FailureReason} {failedOrder.Details} — the store will re-deliver; the server is idempotent.");
                    }
                    break;
                case ConfirmedOrder confirmedOrder:
                    foreach (var item in confirmedOrder.CartOrdered.Items())
                        RemoveProcessingProduct(ResolveProductId(item?.Product));
                    break;
            }
        }

        private void HandlePurchaseFailed(FailedOrder failedOrder)
        {
            if (failedOrder == null) return;
            foreach (var item in failedOrder.CartOrdered.Items())
            {
                var product = item?.Product;
                var productId = ResolveFailureOrDeferredProductId(product);
                ClearAllProcessingProducts();
                EmitPurchaseResult(CreateResult(productId, product?.definition?.storeSpecificId, PurchaseStatus.Failed, failedOrder.FailureReason, failedOrder.Details));
            }
        }

        private void HandlePurchaseDeferred(DeferredOrder deferredOrder)
        {
            if (deferredOrder == null) return;
            foreach (var item in deferredOrder.CartOrdered.Items())
            {
                var product = item?.Product;
                var productId = ResolveFailureOrDeferredProductId(product);
                ClearAllProcessingProducts();
                EmitPurchaseResult(CreateResult(productId, product?.definition?.storeSpecificId, PurchaseStatus.Deferred, PurchaseFailureReason.Unknown, "Purchase deferred (awaiting approval)."));
            }
        }

        private string ResolveProductId(Product product)
        {
            if (product?.definition == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(product.definition.id)) return product.definition.id;
            var byStore = StoreCatalog.Instance.ByStoreProductId(product.definition.storeSpecificId);
            return byStore?.Id ?? string.Empty;
        }

        private string ResolveFailureOrDeferredProductId(Product product)
        {
            var mapped = ResolveProductId(product);
            if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
            if (processingProductIds.Count == 1) return processingProductIds.First();
            return string.Empty;
        }

        // ------------------------------------------------------------------ state + bookkeeping

        private void SetState(InitializationState newState, string message)
        {
            initializationState = newState;
            lastStatusMessage = message ?? string.Empty;
            Log($"state {newState}: {lastStatusMessage}");
            OnInitializationStateChanged?.Invoke(initializationState, lastStatusMessage);
        }

        private void AddProcessingProduct(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return;
            if (processingProductIds.Add(productId)) OnProcessingStateChanged?.Invoke(productId, true);
        }

        private void RemoveProcessingProduct(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return;
            if (processingProductIds.Remove(productId)) OnProcessingStateChanged?.Invoke(productId, false);
        }

        private void ClearAllProcessingProducts()
        {
            if (processingProductIds.Count == 0) return;
            var active = processingProductIds.ToArray();
            foreach (var id in active)
                if (processingProductIds.Remove(id)) OnProcessingStateChanged?.Invoke(id, false);
        }

        private static PurchaseResult CreateResult(string productId, string storeProductId, PurchaseStatus status, PurchaseFailureReason reason, string message, string transactionId = "", StoreRedeemResultData redeem = null)
            => new PurchaseResult
            {
                productId = productId ?? string.Empty,
                storeProductId = storeProductId ?? string.Empty,
                transactionId = transactionId ?? string.Empty,
                status = status,
                failureReason = reason,
                message = message ?? string.Empty,
                redeem = redeem,
            };

        private void EmitPurchaseResult(PurchaseResult result)
        {
            if (result == null) return;
            Log($"purchase {result.productId}: {result.status} ({result.failureReason}) {result.message}");
            OnPurchaseCompleted?.Invoke(result);
        }

        private void Log(string message)
        {
            if (verboseLog) Debug.Log("[IapService] " + message);
        }
    }
}
