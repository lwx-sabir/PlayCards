using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;


namespace Bozo
{
    public class BSMC_Tools : MonoBehaviour
    {
        [MenuItem("Tools/BoZo Tools/Open JSON Location")]
        private static void OpenJsonPath()
        {
            var path = Application.persistentDataPath;
            if (System.IO.Directory.Exists(path))
            {
                Process.Start(path);
            }
        }
    }
}
