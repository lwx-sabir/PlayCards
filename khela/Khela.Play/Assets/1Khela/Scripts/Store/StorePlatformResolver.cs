using Khela.Common.Store;

namespace PlayCard.Store
{
    /// <summary>
    /// Which store this build talks to — decided by the build target, never by config (Unity IAP picks its store module
    /// the same way): Android → Google Play, Apple targets → App Store (StoreKit 2), the Editor and desktop → Unity's
    /// fake store (the server accepts <see cref="StorePlatform.Fake"/> ONLY in Development), WebGL → Web (no Unity IAP;
    /// the store is read-only there until the web checkout exists). Adding a store vendor = a new arm here + a server
    /// verifier + a store-product id per product in the catalog (docs/IAP_SPEC.md §9).
    /// </summary>
    public static class StorePlatformResolver
    {
        public static StorePlatform Current
        {
            get
            {
#if UNITY_EDITOR
                return StorePlatform.Fake;
#elif UNITY_ANDROID
                return StorePlatform.GooglePlay;
#elif UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX
                return StorePlatform.AppStore;
#elif UNITY_WEBGL
                return StorePlatform.Web;
#else
                return StorePlatform.Fake;   // Windows/Linux standalone: Unity IAP runs its fake store
#endif
            }
        }

        /// <summary>Unity IAP can run on this platform (Google Play, App Store, or the fake store).</summary>
        public static bool UnityIapSupported
            => Current == StorePlatform.GooglePlay || Current == StorePlatform.AppStore || Current == StorePlatform.Fake;
    }
}
