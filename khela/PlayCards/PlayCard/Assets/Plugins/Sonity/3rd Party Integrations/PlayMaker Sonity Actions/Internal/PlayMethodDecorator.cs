// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public abstract class PlayMethodDecorator : IMethodSelector {

        protected SoundEventArgumentBuilder soundEventArgs { get; }

        public PlayMethodDecorator(SoundEventArgumentBuilder arguments) {
            this.soundEventArgs = arguments;
        }

        public virtual void SelectMethod() { }

        protected bool HasParameters() => soundEventArgs.WrapperContainer != null;
        protected bool HasTag() => soundEventArgs.SoundTag != null;
    }
}
#endif