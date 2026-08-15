// Khela haptics — native CoreHaptics backend (iOS 13+).
// Ported and hardened from the Watermelon Core Haptic .mm. Key differences vs the source:
//   • stopped/reset handlers RESTART the engine after backgrounding / audio interruption / media-services reset,
//     so haptics survive an app-switch instead of dying silently for the rest of the session.
//   • capabilitiesForHardware.supportsHaptics is checked before creating the engine.
//   • the simple Play path receives a real SHARPNESS (the source hardcoded it to 0) and uses a Transient event for
//     very short taps (crisper, allocation-light) and Continuous for sustained buzzes; RegisterPattern picks the
//     type PER EVENT so a zero-duration pulse can't fail the whole pattern.
//   • players are KEPT ALIVE for the duration of playback — CHHapticEngine does not retain the players it hands
//     out, so a local would be ARC-released the moment the function returns and cut a continuous buzz short.
//   • entry points are namespaced (_KhelaHaptic*) to avoid colliding with other iOS plugins' symbols.

#import <Foundation/Foundation.h>
#import <CoreHaptics/CoreHaptics.h>

API_AVAILABLE(ios(13.0))
static CHHapticEngine *khelaEngine = nil;
static NSMutableDictionary *khelaPatterns = nil;    // registered CHHapticPattern by ID
static NSMutableArray *khelaActivePlayers = nil;    // players held alive while they play
static BOOL khelaStarted = NO;

// Keep a player strongly referenced until playback should be finished, then drop it. The dispatch block is owned by
// the queue (not by the player), so there is no retain cycle: once it fires and is released, the player deallocs.
API_AVAILABLE(ios(13.0))
static void KhelaHoldPlayer(id<CHHapticPatternPlayer> player, double holdSeconds)
{
    if (player == nil) return;
    if (khelaActivePlayers == nil) khelaActivePlayers = [NSMutableArray array];
    [khelaActivePlayers addObject:player];
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(holdSeconds * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        [khelaActivePlayers removeObject:player];
    });
}

// Create the engine on demand and ensure it is running. Returns NO if the hardware can't do haptics or start failed.
API_AVAILABLE(ios(13.0))
static BOOL KhelaEnsureEngine(void)
{
    if (![CHHapticEngine capabilitiesForHardware].supportsHaptics) return NO;

    if (khelaEngine == nil)
    {
        NSError *error = nil;
        khelaEngine = [[CHHapticEngine alloc] initAndReturnError:&error];
        if (error != nil || khelaEngine == nil) { khelaEngine = nil; return NO; }

        // The system stops the engine on backgrounding / interruption; it resets it on media-services loss.
        // Without restarting on these, the first app-switch permanently kills haptics for the session.
        // (khelaStarted is a benign cross-queue flag: worst case one redundant start or one skipped tap right
        //  after a reset — never a crash, and khelaEngine is a never-nilled strong static so weakEngine can't dangle.)
        khelaEngine.stoppedHandler = ^(CHHapticEngineStoppedReason reason) { khelaStarted = NO; };

        __weak CHHapticEngine *weakEngine = khelaEngine;
        khelaEngine.resetHandler = ^{
            khelaStarted = NO;
            NSError *err = nil;
            [weakEngine startAndReturnError:&err];
            if (err == nil) khelaStarted = YES;
        };
    }

    if (!khelaStarted)
    {
        NSError *error = nil;
        [khelaEngine startAndReturnError:&error];
        if (error != nil) return NO;
        khelaStarted = YES;
    }
    return YES;
}

extern "C"
{
    void _KhelaHapticInit(void)
    {
        if (@available(iOS 13.0, *))
        {
            if (khelaPatterns == nil) khelaPatterns = [NSMutableDictionary dictionary];
            if (khelaActivePlayers == nil) khelaActivePlayers = [NSMutableArray array];
            KhelaEnsureEngine();   // prewarm so the first real tap has no cold-start latency
        }
    }

    void _KhelaHapticPlay(float duration, float intensity, float sharpness)
    {
        if (@available(iOS 13.0, *))
        {
            if (!KhelaEnsureEngine()) return;

            NSError *error = nil;
            CHHapticEventParameter *i = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticIntensity value:intensity];
            CHHapticEventParameter *s = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticSharpness value:sharpness];

            // Short taps read better as transients (a crisp click); longer values as a sustained continuous buzz.
            CHHapticEventType type = duration < 0.03f ? CHHapticEventTypeHapticTransient : CHHapticEventTypeHapticContinuous;
            CHHapticEvent *event = [[CHHapticEvent alloc] initWithEventType:type parameters:@[i, s] relativeTime:0 duration:duration];

            CHHapticPattern *pattern = [[CHHapticPattern alloc] initWithEvents:@[event] parameters:@[] error:&error];
            if (error != nil) { NSLog(@"[KhelaHaptic] pattern error: %@", error); return; }

            id<CHHapticPatternPlayer> player = [khelaEngine createPlayerWithPattern:pattern error:&error];
            if (error != nil || player == nil) { NSLog(@"[KhelaHaptic] player error: %@", error); return; }

            KhelaHoldPlayer(player, duration + 0.5);   // keep it alive through playback (+ margin)
            [player startAtTime:0 error:&error];
        }
    }

    void _KhelaHapticRegisterPattern(const char *hapticPatternJson)
    {
        if (@available(iOS 13.0, *))
        {
            if (khelaPatterns == nil) khelaPatterns = [NSMutableDictionary dictionary];

            NSString *jsonString = [NSString stringWithUTF8String:hapticPatternJson];
            NSData *jsonData = [jsonString dataUsingEncoding:NSUTF8StringEncoding];
            NSDictionary *dict = [NSJSONSerialization JSONObjectWithData:jsonData options:0 error:nil];
            if (dict == nil) return;

            NSString *patternId = dict[@"ID"];
            NSArray *eventsArray = dict[@"Pattern"];
            if (patternId == nil || eventsArray == nil) return;

            NSMutableArray *events = [NSMutableArray array];
            for (NSDictionary *eventDict in eventsArray)
            {
                float intensityValue = [eventDict[@"Intensity"] floatValue];
                float sharpnessValue = [eventDict[@"Sharpness"] floatValue];
                float startTimeValue = [eventDict[@"StartTime"] floatValue];
                float durationValue  = [eventDict[@"Duration"] floatValue];

                CHHapticEventParameter *i = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticIntensity value:intensityValue];
                CHHapticEventParameter *s = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticSharpness value:sharpnessValue];

                // Per-event type: a zero/near-zero-duration Continuous event makes initWithEvents: fail and would
                // silently discard the ENTIRE pattern. Transient events ignore duration, so short pulses are safe.
                CHHapticEventType t = durationValue < 0.03f ? CHHapticEventTypeHapticTransient : CHHapticEventTypeHapticContinuous;
                CHHapticEvent *event = [[CHHapticEvent alloc] initWithEventType:t
                                                                     parameters:@[i, s]
                                                                   relativeTime:startTimeValue
                                                                       duration:durationValue];
                [events addObject:event];
            }

            NSError *error = nil;
            CHHapticPattern *pattern = [[CHHapticPattern alloc] initWithEvents:events parameters:@[] error:&error];
            if (error != nil) { NSLog(@"[KhelaHaptic] register error: %@", error); return; }

            khelaPatterns[patternId] = pattern;
        }
    }

    void _KhelaHapticPlayPattern(const char *patternId)
    {
        if (@available(iOS 13.0, *))
        {
            if (!KhelaEnsureEngine() || khelaPatterns == nil) return;

            NSString *idString = [NSString stringWithUTF8String:patternId];
            CHHapticPattern *pattern = khelaPatterns[idString];
            if (pattern == nil) { NSLog(@"[KhelaHaptic] pattern not found: %@", idString); return; }

            NSError *error = nil;
            id<CHHapticPatternPlayer> player = [khelaEngine createPlayerWithPattern:pattern error:&error];
            if (error != nil || player == nil) { NSLog(@"[KhelaHaptic] player error: %@", error); return; }

            KhelaHoldPlayer(player, 5.0);   // generous hold; game haptic patterns are well under this
            [player startAtTime:0 error:&error];
        }
    }
}
