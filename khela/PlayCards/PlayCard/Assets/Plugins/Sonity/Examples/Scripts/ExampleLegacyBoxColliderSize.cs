// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

using UnityEngine;

namespace ExampleSonity {

    [AddComponentMenu("")]
    public class ExampleLegacyBoxColliderSize : MonoBehaviour {

        public Vector3 boxColliderSize = new Vector3(1f, 1f, 1f);

        // Unity has a bug where if you downgrade a project box colliders might set the box colliders to size 2,2,2 instead of 1,1,1.
        private void Start() {
            BoxCollider boxCollider = GetComponent<BoxCollider>();
            if (boxCollider != null ) {
                boxCollider.size = boxColliderSize;
            }
        }
    }
}