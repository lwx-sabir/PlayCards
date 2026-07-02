using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Bozo.ModularCharacters
{
    public interface IOutfitExtension
    {
        string GetID();
        void Initalize(OutfitSystem outfitSystem, Outfit outfit);
        void Execute(OutfitSystem outfitSystem, Outfit outfit);
        //Return Something from this object
        //Great when you have a Custom Map that you need read for the CharacterOptimizer
        object GetValue();
        System.Type GetValueType();
    }

    public interface IOutfitExtension<T> : IOutfitExtension
    {
        //Return Something from this object
        //Great when you have a Custom Map that you need read for the CharacterOptimizer
        new T GetValue();


    }

}
