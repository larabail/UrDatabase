using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Services;

namespace UrDatabase.Models
{
    /// <summary>One drive as the drives dialog shows it.</summary>
    public sealed class DriveRow
    {
        public DriveRow(DriveReport report)
        {
            Report = report ?? throw new ArgumentNullException(nameof(report));
            Title = DriveStatusText.Title(report);
            Body = string.Join(Environment.NewLine, DriveStatusText.Lines(report));
        }

        public DriveReport Report { get; }

        public string Title { get; }

        public string Body { get; }

        /// <summary>Down, and something the launch check raises the alarm about.</summary>
        public bool IsAlarm => Report.RaisesAlarm;

        /// <summary>Down, but nothing the launch check would raise the alarm about.</summary>
        public bool IsQuietlyDown => Report.IsDown && !Report.RaisesAlarm;

        public bool IsUp => !Report.IsDown;

        public bool CanForget => Report.CanForget;

        public static IReadOnlyList<DriveRow> From(IEnumerable<DriveReport>? reports) =>
            DriveStatusText.Ordered(reports ?? Array.Empty<DriveReport>()).Select(r => new DriveRow(r)).ToList();
    }
}
