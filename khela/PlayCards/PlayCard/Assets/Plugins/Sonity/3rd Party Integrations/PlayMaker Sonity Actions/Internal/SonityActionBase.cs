// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

using HutongGames.PlayMaker;
using UnityEngine;
using TooltipAttribute = HutongGames.PlayMaker.TooltipAttribute;

namespace Sonity.PlayMaker.Internal {

    public abstract class SonityActionBase : FsmStateAction {
#region Internal variables
        protected GameObject goLastFrame;
        protected PlayMakerFSM fsm;
        protected SoundEvent m_SoundEvent;
        protected Transform m_Transform;
        protected bool m_EveryFrame = false;
#endregion

        [DisplayOrder(0)]
        [RequiredField]
        [ObjectType(typeof(SoundEvent))]
        [HideIf(nameof(HideSoundEvent))]
        [Title("🔊 Sound Event")]
        [Tooltip("SoundEvent this action interacts with.")]
        public FsmObject fsmSoundEvent;

        [DisplayOrder(1)]
        [RequiredField]
        [HideIf(nameof(HideGameObjectReference))]
        [Title("Target GameObject")]
        [Tooltip("Override GameObject used for Transform Owner")]
        public FsmOwnerDefault gameObjectOverride;

        [DisplayOrder(2)]
        [UIHint(UIHint.FsmName)]
        [HideIf(nameof(HideFsmNameReference))]
        [Tooltip("Optional name of FSM on Game Object")]
        [Title("Target Fsm Name")]
        public FsmString fsmName;

        public override void OnEnter() {
            if (!InitializeSoundEvent()) {
                return;
            }

            InitializeAndCacheTransform();
            DoSoundEventAction();

            if (!m_EveryFrame) {
                Finish();
            }
        }

        protected bool InitializeSoundEvent() {
            if (fsmSoundEvent.IsNone || fsmSoundEvent.Value == null) {
                Debug.LogWarning($"Sound event is not valid. The action will not be performed.");
                return false;
            }

            m_SoundEvent = (SoundEvent)fsmSoundEvent.Value;
            return true;
        }

        protected void InitializeAndCacheTransform() {
            var go = Fsm.GetOwnerDefaultTarget(gameObjectOverride);
            if (go == null) {
                return;
            }

            // Get FSM component if go or fsm name has changed
            if (go != goLastFrame) {
                goLastFrame = go;
                fsm = SonityActionHelpers.GetGameObjectFsm(go, fsmName.Value);
            }

            // Get if value isn't null, then override the owner transform
            if (gameObjectOverride.GameObject.Value != null) {
                m_Transform = gameObjectOverride.GameObject.Value.transform;
            } else {
                m_Transform = Owner.transform;
            }
        }

        protected virtual void DoSoundEventAction() {
            Debug.LogWarning("No override found for Sound Action. Contact the Sonity Developer if the action is not working.");
        }

        public virtual bool HideSoundEvent() => false;
        public virtual bool HideGameObjectReference() => false;

        public virtual bool HideFsmNameReference() =>
            HideGameObjectReference() ? true
            : gameObjectOverride.OwnerOption == OwnerDefaultOption.UseOwner;

        public override void Reset() {
            fsmSoundEvent = new FsmObject();
            fsmName = new FsmString();
            gameObjectOverride = null;
            goLastFrame = null;
        }
    }
}
#endif