// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

using UnityEngine;

namespace ExampleSonity {

    [AddComponentMenu("")]
    public class ExampleWaterPhysics : MonoBehaviour {

        public float buoyancy = 1.5f;
        private float dragMax = 2f;
        private float angularDragBase = 0.05f;
        private float angularDragMax = 0.8f;

        private Transform cachedTransform;

        void Start() {
            cachedTransform = GetComponent<Transform>();
        }

        private void OnTriggerStay(Collider other) {
            Rigidbody rigidbody = other.GetComponent<Rigidbody>();
            if (rigidbody != null && !rigidbody.isKinematic) {
                float waterSurfaceY = cachedTransform.position.y + cachedTransform.lossyScale.y * 0.5f;
                float objectMaxWidth = Mathf.Max(
                    Mathf.Abs(other.transform.lossyScale.x),
                    Mathf.Abs(other.transform.lossyScale.y),
                    Mathf.Abs(other.transform.lossyScale.z)
                    );

                float objectTopY = other.transform.position.y + objectMaxWidth * 0.5f;
                float objectSubmergedAmount = 0f;

                if (objectMaxWidth != 0) {
                    float above = (objectTopY - waterSurfaceY) / objectMaxWidth;
                    objectSubmergedAmount = Mathf.Clamp01(1f - above);
                }

                rigidbody.AddForce(transform.up * Mathf.Abs(Physics.gravity.y) * buoyancy * objectSubmergedAmount, ForceMode.Acceleration);
#if UNITY_6000_0_OR_NEWER
                rigidbody.linearDamping = dragMax * objectSubmergedAmount;
                rigidbody.angularDamping = angularDragBase + ((angularDragMax - angularDragBase) * objectSubmergedAmount);
#else
                rigidbody.drag = dragMax * objectSubmergedAmount;
                rigidbody.angularDrag = angularDragBase + ((angularDragMax - angularDragBase) * objectSubmergedAmount);
#endif
            }
        }

        private void OnTriggerExit(Collider other) {
            Rigidbody rigidbody = other.GetComponent<Rigidbody>();
            if (rigidbody != null && !rigidbody.isKinematic) {
                // Reset drag and angular drag
#if UNITY_6000_0_OR_NEWER
                rigidbody.linearDamping = 0f;
                rigidbody.angularDamping = 0.05f;
#else
                rigidbody.drag = 0f;
                rigidbody.angularDrag = 0.05f;
#endif
            }
        }
    }
}
