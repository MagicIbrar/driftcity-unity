using DriftCity.Game;
using NUnit.Framework;

namespace DriftCity.Tests
{
    /// <summary>Once before every PlayMode test: rewards don't depend on the hour the suite happens to run (golden time
    /// 20-22 adds 50 %); tests that check the bonuses set <see cref="Multipliers.GoldenOverride"/> themselves.</summary>
    [SetUpFixture]
    public class TestDefaults
    {
        [OneTimeSetUp]
        public void CalendarBonusesOff() => Multipliers.GoldenOverride = false;
    }
}
