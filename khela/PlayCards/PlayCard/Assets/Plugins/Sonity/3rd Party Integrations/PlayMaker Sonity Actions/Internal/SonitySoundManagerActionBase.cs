// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

using HutongGames.PlayMaker;
using UnityEngine;
using TooltipAttribute = HutongGames.PlayMaker.TooltipAttribute;

namespace Sonity.PlayMaker.Internal {

    public abstract class SonitySoundManagerActionBase : FsmStateAction {

#region Internal variables
        protected GameObject goLastFrame;
        protected PlayMakerFSM fsm;
        protected SoundManager m_SoundManager;
        protected Transform m_Transform;

        protected bool m_EveryFrame = false;
#endregion

        [RequiredField]
        [DisplayOrder(0)]
        [HideIf(nameof(HideGameObjectReference))]

        [Tooltip("Override GameObject used for Transform Owner")]
        public FsmOwnerDefault gameObjectOverride;

        [DisplayOrder(1)]
        [UIHint(UIHint.FsmName)]
        [HideIf(nameof(HideFsmNameReference))]

        [Tooltip("Optional name of FSM on Game Object")]
        public FsmString fsmName;

        public override void OnEnter() {
            if (SoundManager.Instance == null) {
                Debug.LogWarning("Sound Manager could not be found in scene. Please add one.");
                return;
            }
            m_SoundManager = SoundManager.Instance;

#region Get and cache Game Object's transform

            var go = Fsm.GetOwnerDefaultTarget(gameObjectOverride);
            if (go == null) {
                return;
            }

            if (go != goLastFrame) {
                goLastFrame = go;
                // only get the fsm component if go or fsm name has changed
                fsm = SonityActionHelpers.GetGameObjectFsm(go, fsmName.Value);
            }

            // If the value isn't null, then override the owner transform
            if (gameObjectOverride.GameObject.Value != null) {
                m_Transform = gameObjectOverride.GameObject.Value.transform;
            } else {
                m_Transform = Owner.transform;
            }
#endregion

            DoSoundManagerAction();

            if (!m_EveryFrame) {
                Finish();
            }
        }

        protected virtual void DoSoundManagerAction() {
            Debug.LogWarning("No override found for Sound Action. Contact the Sonity Developer if the action is not working.");
        }

        public virtual bool HideGameObjectReference() => false;
        public virtual bool HideFsmNameReference() =>
            HideGameObjectReference() ? true
            : gameObjectOverride.OwnerOption == OwnerDefaultOption.UseOwner;

        public override void Reset() {
            fsmName = new FsmString();
            gameObjectOverride = null;
            goLastFrame = null;
        }
    }
}
#endif