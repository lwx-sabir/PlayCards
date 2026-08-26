using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// The shop's section tabs over a single scroll view: scrolling lights the tab of the section you are looking at,
    /// tapping a tab scrolls smoothly to that section.
    ///
    /// Both directions drive the same one scroll rect, which is where this kind of control usually goes wrong: a tap
    /// scrolls THROUGH the sections in between, the scroll handler sees each of them arrive, and the tab strip flickers
    /// through every section on the way. So a tap takes ownership — the highlight is pinned to the section being scrolled
    /// to until the scroll finishes, and only then does the scroll position get a say again. A drag cancels the animation
    /// immediately (put this on the same object as the ScrollRect and it hears the drag), because a player who grabs the
    /// list has overruled the tap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopTabs : MonoBehaviour, IBeginDragHandler
    {
        [Serializable]
        public sealed class Tab
        {
            [Tooltip("The tab button. Its onClick is wired at runtime — you do not need to set it in the inspector.")]
            public Button button;
            [Tooltip("The section this tab jumps to: the lane object inside the scroll content.")]
            public RectTransform section;
            [Tooltip("Shown while this is the current section (the lit state). Optional.")]
            public GameObject selectedRoot;
            [Tooltip("Shown while it is NOT current. Optional — leave empty if the lit state is the only difference.")]
            public GameObject unselectedRoot;
        }

        [Header("Scroll")]
        [SerializeField] private ScrollRect scrollRect;
        [Tooltip("The scroll rect's viewport. Empty = taken from the scroll rect.")]
        [SerializeField] private RectTransform viewport;
        [Tooltip("The scroll rect's content. Empty = taken from the scroll rect.")]
        [SerializeField] private RectTransform content;

        [Header("Tabs")]
        [Tooltip("In the order the sections appear down the content.")]
        [SerializeField] private List<Tab> tabs = new List<Tab>();

        [Header("Feel")]
        [Tooltip("Seconds a tab tap takes to travel, however far it goes. 0 = jump.")]
        [SerializeField] private float scrollSeconds = 0.35f;
        [Tooltip("Eases the travel so it settles instead of stopping dead.")]
        [SerializeField]
        private AnimationCurve scrollEase = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("How far below the top of the viewport a section's header counts as \"arrived\". A little slack stops " +
                 "the tab flipping back and forth while a header sits exactly on the boundary.")]
        [SerializeField] private float activateSlack = 24f;
        [Tooltip("Pixels above the section left visible when a tap scrolls to it — room for a header, or just air.")]
        [SerializeField] private float scrollPadding = 8f;

        private int current = -1;
        private bool animating;
        private float animT;
        private float animFrom;
        private float animTo;
        /// <summary>The tab a tap is travelling to. It owns the highlight until it arrives; -1 = the scroll decides.</summary>
        private int pinned = -1;
        /// <summary>Reused by the corner reads: SelectFromScroll runs on every scroll frame, per section.</summary>
        private readonly Vector3[] corners = new Vector3[4];

        /// <summary>Raised when a TAP picks a tab — the sound of choosing, not of the strip following a scroll.</summary>
        public event System.Action<int> TabTapped;

        private void Awake()
        {
            if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
            if (scrollRect != null)
            {
                if (viewport == null) viewport = scrollRect.viewport;
                // A ScrollRect with no Viewport assigned clips to its OWN rect, and that is the rect a section has to
                // arrive at. Without this the tabs go quiet: every measurement needs a viewport, so a null one turns
                // both the highlight and the scroll-to into no-ops with nothing to show for it.
                if (viewport == null) viewport = scrollRect.transform as RectTransform;
                if (content == null) content = scrollRect.content;
            }

            for (int i = 0; i < tabs.Count; i++)
            {
                var tab = tabs[i];
                if (tab?.button == null) continue;
                int index = i;   // captured per tab, not the loop variable
                tab.button.onClick.AddListener(() => GoTo(index));
            }
        }

        private void OnDestroy()
        {
            foreach (var tab in tabs)
                if (tab?.button != null) tab.button.onClick.RemoveAllListeners();
        }

        private void OnEnable()
        {
            if (scrollRect != null) scrollRect.onValueChanged.AddListener(HandleScrolled);
            animating = false;
            pinned = -1;
            current = -1;
            // The layout is usually still dirty on the frame a screen opens — the sections have no real positions until
            // the rebuild, so read them after it rather than lighting whichever tab happens to measure as topmost.
            Canvas.ForceUpdateCanvases();
            SelectFromScroll();
        }

        private void OnDisable()
        {
            if (scrollRect != null) scrollRect.onValueChanged.RemoveListener(HandleScrolled);
            animating = false;
            pinned = -1;
        }

        /// <summary>A drag beats a tap: the player has taken the list back, so stop travelling and follow them.</summary>
        public void OnBeginDrag(PointerEventData eventData)
        {
            animating = false;
            pinned = -1;
        }

        private void HandleScrolled(Vector2 _)
        {
            if (animating || pinned >= 0) return;   // our own travel, not the player's
            SelectFromScroll();
        }

        private void Update()
        {
            if (!animating || content == null) return;

            animT += scrollSeconds > 0f ? Time.unscaledDeltaTime / scrollSeconds : 1f;
            float t = Mathf.Clamp01(animT);
            float eased = scrollEase != null ? scrollEase.Evaluate(t) : t;
            var pos = content.anchoredPosition;
            pos.y = Mathf.LerpUnclamped(animFrom, animTo, eased);
            content.anchoredPosition = pos;

            if (t < 1f) return;
            animating = false;
            // The pin is released only on arrival, so the strip never flickers through the sections passed on the way.
            pinned = -1;
            SelectFromScroll();
        }

        /// <summary>Scroll to a section and light its tab. Public so a deep link ("open the shop on Kash") can call it.</summary>
        public void GoTo(int index)
        {
            if (index < 0 || index >= tabs.Count) return;
            var tab = tabs[index];
            if (tab?.section == null || content == null || viewport == null) return;

            TabTapped?.Invoke(index);
            Select(index);
            pinned = index;

            Canvas.ForceUpdateCanvases();
            float target = Mathf.Clamp(TargetY(tab.section), 0f, MaxScrollY());

            if (scrollSeconds <= 0f)
            {
                var jump = content.anchoredPosition;
                jump.y = target;
                content.anchoredPosition = jump;
                pinned = -1;
                return;
            }

            // Stop the rect's own inertia, or the flick the player was mid-way through fights the travel.
            if (scrollRect != null) scrollRect.velocity = Vector2.zero;
            animFrom = content.anchoredPosition.y;
            animTo = target;
            animT = 0f;
            animating = true;
        }

        /// <summary>Light the tab of the topmost section that has reached the top of the viewport.</summary>
        private void SelectFromScroll()
        {
            if (viewport == null) return;
            float top = viewport.rect.yMax - activateSlack;

            int found = -1;
            for (int i = 0; i < tabs.Count; i++)
            {
                var section = tabs[i]?.section;
                if (section == null || !section.gameObject.activeInHierarchy) continue;
                // Sections move UP as you scroll down, so a section has "arrived" once its top edge is at or above the
                // viewport top. The ones still below have a LOWER y and fail this, so the last one that passes is the
                // deepest section you have actually scrolled into — the one being read.
                if (SectionTopInViewport(section) >= top) found = i;
            }
            Select(found >= 0 ? found : FirstLiveTab());
        }

        private int FirstLiveTab()
        {
            for (int i = 0; i < tabs.Count; i++)
                if (tabs[i]?.section != null && tabs[i].section.gameObject.activeInHierarchy) return i;
            return -1;
        }

        private void Select(int index)
        {
            if (index == current) return;
            current = index;
            for (int i = 0; i < tabs.Count; i++)
            {
                var tab = tabs[i];
                if (tab == null) continue;
                bool on = i == index;
                if (tab.selectedRoot != null) tab.selectedRoot.SetActive(on);
                if (tab.unselectedRoot != null) tab.unselectedRoot.SetActive(!on);
            }
        }

        /// <summary>The section's TOP edge, in the viewport's own local space (+y is up, 0 is the viewport centre).</summary>
        private float SectionTopInViewport(RectTransform section)
        {
            section.GetWorldCorners(corners);   // 0 bottom-left, 1 TOP-left, 2 top-right, 3 bottom-right
            return viewport.InverseTransformPoint(corners[1]).y;
        }

        /// <summary>The content anchoredPosition.y that puts <paramref name="section"/> at the top of the viewport.</summary>
        private float TargetY(RectTransform section)
        {
            // Measured through the viewport rather than from anchoredPosition: the sections sit inside nested layout
            // groups, so their own anchored positions say nothing useful about where they land on screen.
            float delta = SectionTopInViewport(section) - (viewport.rect.yMax - scrollPadding);
            return content.anchoredPosition.y - delta;
        }

        private float MaxScrollY()
        {
            if (content == null || viewport == null) return 0f;
            return Mathf.Max(0f, content.rect.height - viewport.rect.height);
        }
    }
}
