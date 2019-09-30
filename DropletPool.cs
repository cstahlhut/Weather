using System;
using System.Collections.Generic;
using AtmosphericDamage;
using Havok;
using VRage.Collections;
using VRage.Library.Threading;
using VRageMath;

namespace AtmosphereDamage
{
    public class DropletPool
    {
        private SpinLockRef _activeLock = new SpinLockRef();
        private Droplet[] _unused;
        private List<Droplet> _active;
        private int _baseCapacity;
        private int currentUsed;

        public SpinLockRef Lock
        {
            get { return _activeLock; }
        }

        public List<Droplet> Active
        {
            get
            {
                using (_activeLock.Acquire())
                {
                    return new List<Droplet>(_active);
                }
            }
        }

        public DropletPool(int baseCapacity)
        {
            _baseCapacity = baseCapacity;
            _unused = new Droplet[baseCapacity];
            _active = new List<Droplet>();
            for (int i = 0; i < _baseCapacity; i++)
                _unused[i] = new Droplet();
        }

        /// <summary>Returns true when new item was allocated</summary>
        public bool AllocateOrCreate(out Droplet droplet)
        {
            using (_activeLock.Acquire())
            {
                var flag = currentUsed < _baseCapacity;
                droplet = flag ? _unused[currentUsed++] : IncreaseQueueSize();
                _active.Add(droplet);
                return flag;
            }
        }

        public void DeallocateAll()
        {
            using (_activeLock.Acquire())
            {
                _active.Clear();
                currentUsed = 0;
            }
        }

        public Droplet IncreaseQueueSize()
        {
            using (_activeLock.Acquire())
            {
                _baseCapacity += 10000;
                Array.Resize(ref _unused, _baseCapacity);
                for (int i = currentUsed++; i < _unused.Length; i++)
                {
                    _unused[i] = new Droplet();
                }

                return _unused[currentUsed];
            }
        }
    }

    public class Droplet
    {
        public Vector3D StartPoint;
        public Vector3D Direction;
        public float DrawLength;
        public Color LineColor;
    }
}
