using UnityEngine;
using UnityEngine.EventSystems;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Bozo.Utilities 
{

    public class BoZo_EventSystemHandler : MonoBehaviour
    {
        private void Awake()
        {
#if ENABLE_INPUT_SYSTEM

            var newModule = GetComponent<InputSystemUIInputModule>();
            var oldModule = GetComponent<StandaloneInputModule>();

            if (newModule == null)
            {
                newModule = gameObject.AddComponent<InputSystemUIInputModule>();
            }

            if (oldModule != null)
            {
                oldModule.enabled = false;
            }

            newModule.enabled = true;
#endif
        }
    }
}

