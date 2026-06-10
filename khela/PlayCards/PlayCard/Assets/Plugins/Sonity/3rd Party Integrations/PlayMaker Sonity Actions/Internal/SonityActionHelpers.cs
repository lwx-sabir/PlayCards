// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

using UnityEngine;

namespace Sonity.PlayMaker.Internal {
    public static class SonityActionHelpers {
        public static PlayMakerFSM GetGameObjectFsm(GameObject obj, string fsmName) {
            if (!string.IsNullOrEmpty(fsmName)) {
                var fsmComponents = obj.GetComponents<PlayMakerFSM>();

                foreach (var fsmComponent in fsmComponents) {
                    if (fsmComponent.FsmName == fsmName) {
                        return fsmComponent;
                    }
                }
                Debug.LogWarning("Could not find FSM: " + fsmName + " on GameObject: " + obj.name);
            }
            return obj.GetComponent<PlayMakerFSM>();
        }
    }
}
#endif