using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

namespace Unity.Entities
{
    internal struct SortingUtilities
    {
        public static unsafe void InsertSorted(ComponentType* data, int length, ComponentType newValue)
        {
            while (length > 0 && newValue < data[length - 1])
            {
                data[length] = data[length - 1];
                --length;
            }

            data[length] = newValue;
        }

        public static unsafe void InsertSorted(ComponentTypeInArchetype* data, int length, ComponentType newValue)
        {
            var newVal = new ComponentTypeInArchetype(newValue);
            while (length > 0 && newVal < data[length - 1])
            {
                data[length] = data[length - 1];
                --length;
            }

            data[length] = newVal;
        }
    }
}