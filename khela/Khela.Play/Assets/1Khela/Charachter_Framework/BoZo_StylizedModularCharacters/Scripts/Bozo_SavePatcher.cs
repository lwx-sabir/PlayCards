using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Bozo.ModularCharacters
{
    public static class Bozo_SavePatcher
    {
        public static CharacterData UpdateSave(CharacterData data)
        {
            if (data.versionID < 1)
            {
                data = Update1(data);
            }

            return data;
        }

        private static CharacterData Update1(CharacterData data)
        {
            OutfitData pupilOutfit = new OutfitData();
            OutfitData irisOutfit = new OutfitData();
            OutfitData UnderTopOutfit = new OutfitData();
            OutfitData UnderBottomOutfit = new OutfitData();

            OutfitData eyes = new OutfitData();
            int eyesIndex = 0;
            OutfitData body = new OutfitData();
            int bodyIndex = 0;
            OutfitData head = new OutfitData();
            int headIndex = 0;



            for (int i = 0; i < data.outfitDatas.Count; i++)
            {
                if (data.outfitDatas[i].outfit.Contains("Eyes/"))
                {
                    eyes = data.outfitDatas[i];
                    eyesIndex = i;
                }
                if (data.outfitDatas[i].outfit.Contains("Body/"))
                {
                    body = data.outfitDatas[i];
                    bodyIndex = i;
                }
                if (data.outfitDatas[i].outfit.Contains("Head/"))
                {
                    head = data.outfitDatas[i];
                    headIndex = i;
                }
            }

            //Body Changes
            if (body.pattern == "Pattern/Underwear_Boxers_D")
            {
                UnderTopOutfit.outfit = "";
                UnderBottomOutfit.outfit = "UnderLower/UnderLower_SimpleBoxers";
            }
            if (body.pattern == "Pattern/Underwear_BraPanties_D")
            {
                UnderTopOutfit.outfit = "UnderUpper/UnderUpper_SimpleBra";
                UnderBottomOutfit.outfit = "UnderLower/UnderLower_SimplePanties";
            }
            if (body.pattern == "Pattern/Underwear_Neutral_D")
            {
                UnderTopOutfit.outfit = "UnderUpper/UnderUpper_SimpleUnderShirt";
                UnderBottomOutfit.outfit = "UnderLower/UnderLower_SimpleBoxers";
            }


            //Eye Changes
            var pupil = eyes.decal;
            if (pupil != "")
            {
                pupil = pupil.Replace("Decal", "Pupil");

                pupilOutfit.outfit = pupil;
                pupilOutfit.colors = eyes.decalColors;

                eyes.decal = "";
            }
            var iris = eyes.pattern;
            if (pupil != "")
            {
                iris = iris.Replace("Pattern", "Eyes");

                irisOutfit.outfit = iris;
                irisOutfit.colors = eyes.patternColors;

                eyes.pattern = "";
            }

            head.colors[6] = eyes.colors[0];
            head.colors[7] = eyes.colors[1];



            UnderBottomOutfit.colors = body.patternColors;
            UnderTopOutfit.colors = body.patternColors;

            body.pattern = "";
            body.decal = "";


            if (pupilOutfit.outfit != "") data.outfitDatas.Add(pupilOutfit);
            if (irisOutfit.outfit != "") data.outfitDatas.Add(irisOutfit);
            if (UnderTopOutfit.outfit != "") data.outfitDatas.Add(UnderTopOutfit);
            if (UnderBottomOutfit.outfit != "") data.outfitDatas.Add(UnderBottomOutfit);

            data.outfitDatas[eyesIndex] = eyes;
            data.outfitDatas[headIndex] = head;
            data.outfitDatas[bodyIndex] = body;

            data.outfitDatas.RemoveAt(eyesIndex);

            data.bodyShapes.Add(100); //Neck
            data.bodyIDs.Add("NeckThickness"); //Neck




            data.versionID = 1;
            return data;
        }
    }
}
