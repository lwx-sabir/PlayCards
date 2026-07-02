using UnityEngine;

namespace Bozo.ModularCharacters
{
    public class ExpressionSelect : MonoBehaviour
    {
        public OutfitSystem outfitSystem;
        public Animator animator;

        public string parameterID;

        private void OnEnable()
        {
            if(outfitSystem) animator = outfitSystem.animator;
        }

        public void SetExpression(float value)
        {
            if (!animator) return;

            animator.SetFloat(parameterID, value);
        }
    }
}