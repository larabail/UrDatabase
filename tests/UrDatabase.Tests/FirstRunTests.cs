using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// When the setup screen is offered. Three of these four tests are about *not* offering it:
    /// the cost of a wizard appearing in front of somebody's existing library is far higher than
    /// the cost of a new user having to find the Settings button.
    /// </summary>
    public class FirstRunTests
    {
        [Fact]
        public void A_machine_that_has_never_run_the_app_is_offered_setup()
        {
            Assert.True(FirstRun.IsSetupNeeded(setupCompleted: false, hasConfigFile: false, hasDatabase: false));
        }

        [Fact]
        public void Setup_is_never_offered_twice()
        {
            Assert.False(FirstRun.IsSetupNeeded(setupCompleted: true, hasConfigFile: true, hasDatabase: false));
        }

        [Fact]
        public void An_install_configured_by_hand_is_left_alone()
        {
            // A file written before this screen existed carries no completion flag. Asking its
            // owner to introduce themselves would look like the app had lost their settings.
            Assert.False(FirstRun.IsSetupNeeded(setupCompleted: false, hasConfigFile: true, hasDatabase: false));
        }

        [Fact]
        public void An_existing_catalogue_counts_as_having_been_set_up()
        {
            // Someone who has already scanned a library has answered the only question setup
            // asks, whatever their configuration file does or does not say.
            Assert.False(FirstRun.IsSetupNeeded(setupCompleted: false, hasConfigFile: false, hasDatabase: true));
        }
    }
}
