using System;
using System.Collections.Generic;
using System.Diagnostics;

/* Pulled from DarkStar weaponCore - all credit for code is his */

namespace AtmosphericDamage
{
    internal class DSUtils
    {
        internal struct Results
        {
            public double Min;
            public double Max;
            public double Median;
        }

        internal class Timings
        {
            public double Max;
            public double Min;
            public double Total;
            public double Average;
            public int Events;
            public readonly List<int> Values = new List<int>();
            public int[] TmpArray = new int[1];

            internal void Clean()
            {
                Max = 0;
                Min = 0;
                Total = 0;
                Average = 0;
                Events = 0;
            }
        }

        private double _last;
        private bool _time;
        private Stopwatch Sw { get; } = new Stopwatch();
        private readonly Dictionary<string, Timings> _timings = new Dictionary<string, Timings>();
        public void Start(string name, bool time = true)
        {
            _time = time;
            Sw.Restart();
        }

        public void Clear()
        {
            _timings.Clear();
        }

        public void Clean()
        {
            foreach (var timing in _timings.Values)
                timing.Clean();
        }

        public Results GetValue(string name)
        {
            Timings times;
            if (_timings.TryGetValue(name, out times))
            {
                var itemCnt = times.Values.Count;
                var tmpCnt = times.TmpArray.Length;
                if (itemCnt != tmpCnt)
                    Array.Resize(ref times.TmpArray, itemCnt);
                for (int i = 0; i < itemCnt; i++)
                    times.TmpArray[i] = times.Values[i];

                times.Values.Clear();
                var median = GetMedian(times.TmpArray);

                return new Results { Median = median / 1000000.0, Min = times.Min, Max = times.Max };
            }

            return new Results();
        }

        public void Complete(string name, bool store, bool display = false)
        {
            Sw.Stop();
            var ticks = Sw.ElapsedTicks;
            var ns = 1000000000.0 * ticks / Stopwatch.Frequency;
            var ms = ns / 1000000.0;
            if (store)
            {
                Timings timings;
                if (_timings.TryGetValue(name, out timings))
                {
                    timings.Total += ms;
                    timings.Values.Add((int)ns);
                    timings.Events++;
                    timings.Average = (timings.Total / timings.Events);
                    if (ms > timings.Max) timings.Max = ms;
                    if (ms < timings.Min || timings.Min <= 0) timings.Min = ms;
                }
                else
                {
                    timings = new Timings();
                    timings.Total += ms;
                    timings.Values.Add((int)ns);
                    timings.Events++;
                    timings.Average = ms;
                    timings.Max = ms;
                    timings.Min = ms;
                    _timings[name] = timings;
                }
            }
            Sw.Reset();
            if (display)
            {
                var message = $"{(name)} ms:{(float)ms} last-ms:{(float)_last}";
                _last = ms;
                if (_time) Logging.Instance.WriteLine(message);
                else Logging.Instance.WriteLine(message);
            }
        }

        public static double GetMedian(int[] array)
        {
            int[] tempArray = array;
            int count = tempArray.Length;

            Array.Sort(tempArray);

            double medianValue = 0;

            if (count % 2 == 0)
            {
                // count is even, need to get the middle two elements, add them together, then divide by 2
                int middleElement1 = tempArray[(count / 2) - 1];
                int middleElement2 = tempArray[(count / 2)];
                medianValue = (middleElement1 + middleElement2) / 2;
            }
            else
            {
                // count is odd, simply get the middle element.
                medianValue = tempArray[(count / 2)];
            }

            return medianValue;
        }
    }

    internal class RunningAverage
    {
        private readonly int _size;
        private readonly int[] _values;
        private int _valuesIndex;
        private int _valueCount;
        private int _sum;

        internal RunningAverage(int size)
        {
            _size = Math.Max(size, 1);
            _values = new int[_size];
        }

        internal int Add(int newValue)
        {
            // calculate new value to add to sum by subtracting the 
            // value that is replaced from the new value; 
            var temp = newValue - _values[_valuesIndex];
            _values[_valuesIndex] = newValue;
            _sum += temp;

            _valuesIndex++;
            _valuesIndex %= _size;

            if (_valueCount < _size)
                _valueCount++;

            return _sum / _valueCount;
        }
    }



}
    