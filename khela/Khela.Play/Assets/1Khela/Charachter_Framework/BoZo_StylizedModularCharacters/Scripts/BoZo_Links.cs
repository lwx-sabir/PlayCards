using UnityEngine;


namespace Bozo
{

public class BoZo_Links : MonoBehaviour
{

        private void Awake()
        {
    #if !UNITY_EDITOR
            Destroy(this.gameObject);
    #endif
        }
        public void Documentation()
        {
            Application.OpenURL("https://docs.google.com/document/d/1VV8QwJyNBo56hBcqAr9qMW6Snj_iiq9VLEw-i6CrbD0/edit?usp=sharing");
        }

        public void Discord()
        {
            Application.OpenURL("https://discord.gg/UCbRjUy7m7"); 
        }

        public void Twitter()
        {

        }

        public void Youtube()
        {

        }
    }

}