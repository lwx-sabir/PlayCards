#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace TheMightyCat
{
    [CustomEditor(typeof(MightyDisplay))]
    public class MightyDisplayPlacerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            MightyDisplay myScript = (MightyDisplay)target;
            if (GUILayout.Button("OverlapAndPlaceGrid"))
            {
                myScript.OverlapAndPlaceGrid();
            }

            if (GUILayout.Button("ScaleMesh"))
            {
                myScript.CenterAndScaleMesh();
            }
        }
    }

    public class MightyDisplay : MonoBehaviour
    {
        public Vector3 boxSize = Vector3.one; // Size of the box for overlap
        public LayerMask layerMask; // Layer mask to filter overlap results
        public int columns = 3; // Number of columns in the grid
        public float spacing = 1.5f; // Spacing between characters in the grid
        [SerializeField]
        private string category = "Player"; // Tag to filter the overlapping objects

        [FormerlySerializedAs("MeshObject")] [SerializeField]
        private GameObject meshObject;
        
        public string Category
        {
            get { return category; }
            set
            {
                if (category != value)
                {
                    category = value;
                    UpdateText();
                }
            }
        }
        
        void OnValidate()
        {
            UpdateText();
        }
        
        private void UpdateText()
        {
            TextMeshPro tmPro = GetComponentInChildren<TextMeshPro>();
            if (tmPro)
            {
                tmPro.text = Category;
            }
        }

        public void OverlapAndPlaceGrid()
        {
            Collider[] allOverlaps = Physics.OverlapBox(transform.position, boxSize / 2, transform.rotation, layerMask);

            Debug.Log($"Found {allOverlaps.Length} overlapping colliders.");
            
            GameObject[] displayCharacters = new GameObject[allOverlaps.Length];
            int index = 0;
            foreach (Collider hitCollider in allOverlaps)
            {
                Debug.Log($"Overlapping with {hitCollider.name}");
                
                if (hitCollider.gameObject.CompareTag("Player"))
                {
                    displayCharacters[index] = hitCollider.gameObject;
                    index++;
                }
            }
            
            Array.Resize(ref displayCharacters, index);
            
            DoPlacement(displayCharacters);
            
            DrawDebugBox();
        }

        private void DoPlacement(GameObject[] displayCharacters)
        {
            Vector3 desiredPosition = transform.position;
            Quaternion desiredRotation = transform.rotation;
            
            for (int i = 0; i < displayCharacters.Length; i++)
            {
                int currentColumn = i % columns;
                int currentRow = i / columns;
                
                Vector3 offset = new Vector3(-currentColumn * spacing, 0, -currentRow * spacing);
                Vector3 finalPosition = desiredPosition + offset;
                
                displayCharacters[i].transform.position = finalPosition;
                displayCharacters[i].transform.rotation = desiredRotation;
                
                // attach to self
                displayCharacters[i].transform.parent = transform;

            }
        }

        private void DrawDebugBox()
        {
            // Draw the box using Debug.DrawLine
            Vector3[] corners = GetBoxCorners(transform.position, boxSize, transform.rotation);
            for (int i = 0; i < 4; i++)
            {
                Debug.DrawLine(corners[i], corners[(i + 1) % 4], Color.red, 2f); // Bottom square
                Debug.DrawLine(corners[i + 4], corners[((i + 1) % 4) + 4], Color.red, 2f); // Top square
                Debug.DrawLine(corners[i], corners[i + 4], Color.red, 2f); // Vertical lines
            }
        }

        private Vector3[] GetBoxCorners(Vector3 center, Vector3 size, Quaternion rotation)
        {
            Vector3[] corners = new Vector3[8];
            Vector3 extents = size / 2;

            // Bottom corners
            corners[0] = center + rotation * new Vector3(-extents.x, -extents.y, -extents.z);
            corners[1] = center + rotation * new Vector3(extents.x, -extents.y, -extents.z);
            corners[2] = center + rotation * new Vector3(extents.x, -extents.y, extents.z);
            corners[3] = center + rotation * new Vector3(-extents.x, -extents.y, extents.z);

            // Top corners
            corners[4] = center + rotation * new Vector3(-extents.x, extents.y, -extents.z);
            corners[5] = center + rotation * new Vector3(extents.x, extents.y, -extents.z);
            corners[6] = center + rotation * new Vector3(extents.x, extents.y, extents.z);
            corners[7] = center + rotation * new Vector3(-extents.x, extents.y, extents.z);

            return corners;
        }

        private void OnDrawGizmosSelected()
        {
            // Draw the box in the editor using Gizmos
            Gizmos.color = Color.green;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, boxSize);
        }

        public void CenterAndScaleMesh()
        {
            Transform[] children = GetComponentsInChildren<Transform>();
    
            // Loop through all children and find their bounds
            List<Bounds> boundsList = new List<Bounds>();
            foreach (Transform child in children)
            {
                if (child.gameObject != meshObject && child.gameObject != gameObject)
                {
                    Renderer skinnedRenderer = child.GetComponentInChildren<Renderer>();
                    if (skinnedRenderer != null) 
                    {
                        boundsList.Add(skinnedRenderer.bounds);
                    }
                }
            }

            if (boundsList.Count == 0) return;  // Exit if no bounds found
            
            Bounds combinedBounds = boundsList[0];
            foreach (Bounds bounds in boundsList)
            {
                combinedBounds.Encapsulate(bounds);
            }
            
            const float offset = 1.4f;
            meshObject.transform.position = new Vector3(combinedBounds.center.x, meshObject.transform.position.y, combinedBounds.center.z);
            meshObject.transform.localScale = new Vector3(combinedBounds.size.x + offset, meshObject.transform.localScale.y, combinedBounds.size.z + offset);
            
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }
    }
}
#endif
