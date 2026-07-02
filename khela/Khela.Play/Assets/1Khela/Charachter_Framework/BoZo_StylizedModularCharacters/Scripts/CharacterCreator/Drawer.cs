using UnityEngine;
using UnityEngine.EventSystems;

namespace Bozo.ModularCharacters
{


    public class Drawer : MonoBehaviour, IDeselectHandler
    {
        public RectTransform scroll;

        private Vector3 target;

        private float dur = 1;
        private float time = 1;


        private void OnEnable()
        {
            scroll.localPosition = new Vector3(1000, scroll.localPosition.y, scroll.localPosition.z);
            //Invoke("Close", 0.01f);
        }

        public void Open()
        {
            target = new Vector3(0, scroll.localPosition.y, scroll.localPosition.z);
            time = 0;
        }

        public void Close()
        {
            target = new Vector3(scroll.rect.width, scroll.localPosition.y, scroll.localPosition.z);
            time = 0;
        }

        private void Update()
        {
            if (time < dur)
            {
                var t = time / dur;
                time += Time.deltaTime;
                scroll.localPosition = Vector3.Lerp(scroll.localPosition, target, t);
            }
        }

        public void OnDeselect(BaseEventData eventData)
        {
            Invoke("Close", 0.2f);
        }
    }
}
