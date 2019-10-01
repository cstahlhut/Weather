using System.Diagnostics;

/* Pulled from DarkStar WeaponCore - all credit for code is his */

namespace AtmosphericDamage
{
    internal class DSUtils
    {
        private double _last;
        private string _message;
        private bool _time;
        private Stopwatch Sw { get; } = new Stopwatch();

        public void Start(string message, bool time = true)
        {
            _message = message;
            _time = time;
            Sw.Restart();
        }

        public void Complete(bool display = true)
        {
            Sw.Stop();
            var ticks = Sw.ElapsedTicks;
            var ns = 1000000000.0 * ticks / Stopwatch.Frequency;
            var ms = ns / 1000000.0;
            var s = ms / 1000;
            Sw.Reset();
            var message = $"{_message} ms:{(float)ms} last-ms:{(float)_last} s:{(int)s}";
            if (ms > 0.1) Logging.Instance.WriteLine(message + " -- BAD CODE!!");
            else if (_time && display) Logging.Instance.WriteLine(message);
            else if (display) Logging.Instance.WriteLine(message);
            _last = ms;
        }
    }
}
    