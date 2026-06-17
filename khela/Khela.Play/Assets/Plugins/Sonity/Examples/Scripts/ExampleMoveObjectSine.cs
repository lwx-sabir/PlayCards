// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

using UnityEngine;

namespace ExampleSonity {

    [AddComponentMenu("")]
    public class ExampleMoveObjectSine : MonoBehaviour {
        public float frequencyHertz = 1f;
        public Vector3 endPositionOffset;
        private Vector3 endPosition;
        private Vector3 startPosition;
        private float elapsedTime = 0f;

        public void OnEnable() {
            startPosition = transform.position;
            endPosition = transform.position + endPositionOffset;
        }

        public void FixedUpdate() {
            elapsedTime += Time.fixedDeltaTime;
            transform.position = startPosition + (endPosition - startPosition) * 0.5f * (1 - Mathf.Cos(2.0f * Mathf.PI * frequencyHertz * elapsedTime));
        }
    }
}