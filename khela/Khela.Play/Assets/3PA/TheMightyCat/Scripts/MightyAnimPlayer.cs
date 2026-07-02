using UnityEngine;

namespace TheMightyCat
{
    public class MightyAnimPlayer : MonoBehaviour
    {
        public AnimationClip animationClip;
        private Animator _animatorComp;
        void Start()
        {
            _animatorComp = GetComponent<Animator>();

            if (!_animatorComp)
            {
                Debug.LogError("Animator component not found on this GameObject!");
                return;
            }
            
            if (animationClip)
            {
                // Create an AnimatorOverrideController to override the base controller
                var overrideController = new AnimatorOverrideController(_animatorComp.runtimeAnimatorController);
                
                // Assign the animation clip to the override controller
                overrideController["clip"] = animationClip;
                animationClip.wrapMode = WrapMode.Loop;
                
                _animatorComp.runtimeAnimatorController = overrideController;
                _animatorComp.Play("PlayAnimation", 0, 0f);
            }
            else
            {
                Debug.LogError("No animation clip assigned!");
            }
        }
    }
}