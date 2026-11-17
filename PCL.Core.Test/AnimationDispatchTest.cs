using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static PCL.ModAnimation;

namespace PCL.Core.Test;

[TestClass]
[DoNotParallelize]
public sealed class AnimationDispatchTest
{
    [TestMethod]
    public void AGroupStartedBetweenFallbackFramesDoesNotInheritElapsedTimeBeforeItsStart()
    {
        Network.DownloadTestEnvironment.EnsureInitialized();
        var originalGroups = aniGroups;
        var previousSpeed = aniSpeed;
        try
        {
            aniSpeed = 1;
            aniGroups = new ConcurrentDictionary<string, AniGroupEntry>();
            var callbacks = 0;
            aniGroups["late-start"] = new AniGroupEntry
            {
                startTick = 99,
                data = [AaCode(() => callbacks++, 150)]
            };
            AniTimer(100, 100);
            AniTimer(100, 200);
            Assert.AreEqual(0, callbacks);
            AniTimer(49, 249);
            Assert.AreEqual(0, callbacks, "The full 150 ms delay must elapse after the group starts.");
            AniTimer(1, 250);
            Assert.AreEqual(1, callbacks);
        }
        finally
        {
            aniGroups = originalGroups;
            aniSpeed = previousSpeed;
        }
    }

    [TestMethod]
    public void CompletingAnAnimationPreservesItsReplacementUntilNextFrame()
    {
        Network.DownloadTestEnvironment.EnsureInitialized();
        var originalGroups = aniGroups;
        var previousSpeed = aniSpeed;
        try
        {
            aniSpeed = 1;
            aniGroups = new ConcurrentDictionary<string, AniGroupEntry>();
            var replacementRuns = 0;
            var replacement = new AniGroupEntry
            {
                startTick = 0,
                data = [AaCode(() => replacementRuns++)]
            };
            aniGroups["same-name"] = new AniGroupEntry
            {
                startTick = 0,
                data = [AaCode(() => aniGroups["same-name"] = replacement)]
            };

            AniTimer(20, 20);
            Assert.IsTrue(aniGroups.TryGetValue("same-name", out var remaining));
            Assert.AreSame(replacement, remaining);
            Assert.AreEqual(0, replacementRuns);

            AniTimer(20, 40);
            Assert.AreEqual(1, replacementRuns);
            Assert.IsEmpty(aniGroups);
        }
        finally
        {
            aniGroups = originalGroups;
            aniSpeed = previousSpeed;
        }
    }
}
