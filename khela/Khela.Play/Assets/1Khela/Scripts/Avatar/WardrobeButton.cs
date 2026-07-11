using PlayCard.App;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>Drop on a Home "Customize" button; hook its OnClick → <see cref="Open"/>. (A relay so a UI Button can
    /// reach the static <see cref="SceneNavigator.GoToWardrobe"/>.)</summary>
    public sealed class WardrobeButton : MonoBehaviour
    {
        public void Open() => SceneNavigator.GoToWardrobe();
    }
}
