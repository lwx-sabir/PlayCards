using System.Collections.Generic;
using UnityEngine;


namespace Bozo.ModularCharacters
{
    public abstract class OutfitBase : MonoBehaviour
    {


        private void Awake()
        {
            //material = GetComponentInChildren<Renderer>().material;
        }

        public virtual void SetSwatch(int swatchIndex, bool linkedChange = false)
        {

        }


    }
}
