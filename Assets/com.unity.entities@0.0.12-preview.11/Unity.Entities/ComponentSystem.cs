using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Collections.Generic;

namespace Unity.Entities
{
    public unsafe abstract class ComponentSystemBase : ScriptBehaviourManager
    {
        InjectComponentGroupData[] m_InjectedComponentGroups;
        InjectFromEntityData m_InjectFromEntityData;

        ComponentGroupArrayStaticCache[] m_CachedComponentGroupArrays;
        ComponentGroup[] m_ComponentGroups;

        NativeList<int> m_JobDependencyForReadingManagers;
        NativeList<int> m_JobDependencyForWritingManagers;

        internal int* m_JobDependencyForReadingManagersPtr;
        internal int m_JobDependencyForReadingManagersLength;

        internal int* m_JobDependencyForWritingManagersPtr;
        internal int m_JobDependencyForWritingManagersLength;

        uint m_LastSystemVersion;

        internal ComponentJobSafetyManager m_SafetyManager;
        internal EntityManager m_EntityManager;
        World m_World;

        bool m_AlwaysUpdateSystem;
        internal bool m_PreviouslyEnabled;

        public bool Enabled { get; set; } = true;
        public ComponentGroup[] ComponentGroups => m_ComponentGroups;

        public uint GlobalSystemVersion => m_EntityManager.GlobalSystemVersion;

        public bool ShouldRunSystem()
        {
            if (!m_World.IsCreated)
                return false;

            if (m_AlwaysUpdateSystem)
                return true;

            var length = m_ComponentGroups?.Length ?? 0;

            if (length == 0)
                return true;

            // If all the groups are empty, skip it.
            // (There’s no way to know what they key value is without other markup)
            for (int i = 0; i != length; i++)
            {
                if (!m_ComponentGroups[i].IsEmptyIgnoreFilter)
                    return true;
            }

            return false;
        }

        protected override void OnBeforeCreateManagerInternal(World world, int capacity)
        {
            m_World = world;
            m_EntityManager = world.GetOrCreateManager<EntityManager>();
            m_SafetyManager = m_EntityManager.ComponentJobSafetyManager;
            m_AlwaysUpdateSystem = GetType().GetCustomAttributes(typeof(AlwaysUpdateSystemAttribute), true).Length != 0;

            m_ComponentGroups = new ComponentGroup[0];
            m_CachedComponentGroupArrays = new ComponentGroupArrayStaticCache[0];
            m_JobDependencyForReadingManagers = new NativeList<int>(10, Allocator.Persistent);
            m_JobDependencyForWritingManagers = new NativeList<int>(10, Allocator.Persistent);

            ComponentSystemInjection.Inject(this, world, m_EntityManager, out m_InjectedComponentGroups, out m_InjectFromEntityData);
            m_InjectFromEntityData.ExtractJobDependencyTypes(this);

            UpdateInjectedComponentGroups();
        }

        protected sealed override void OnAfterDestroyManagerInternal()
        {
            foreach (var group in m_ComponentGroups)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                group.DisallowDisposing = null;
#endif
                group.Dispose();
            }
            m_ComponentGroups = null;
            m_InjectedComponentGroups = null;
            m_CachedComponentGroupArrays = null;

            if (m_JobDependencyForReadingManagers.IsCreated)
                m_JobDependencyForReadingManagers.Dispose();
            if (m_JobDependencyForWritingManagers.IsCreated)
                m_JobDependencyForWritingManagers.Dispose();
        }

        protected override void OnBeforeDestroyManagerInternal()
        {
            if (m_PreviouslyEnabled)
            {
                m_PreviouslyEnabled = false;
                OnStopRunning();
            }
            CompleteDependencyInternal();
            UpdateInjectedComponentGroups();
        }

        protected virtual void OnStartRunning()
        {

        }

        protected virtual void OnStopRunning()
        {

        }

        protected internal void BeforeUpdateVersioning()
        {
            m_EntityManager.Entities->IncrementGlobalSystemVersion();
            foreach (var group in m_ComponentGroups)
                group.SetFilterChangedRequiredVersion(m_LastSystemVersion);
        }

        protected internal void AfterUpdateVersioning()
        {
            m_LastSystemVersion = EntityManager.Entities->GlobalSystemVersion;
        }

        protected EntityManager EntityManager => m_EntityManager;
        protected World World => m_World;

        // TODO: this should be made part of UnityEngine?
        static void ArrayUtilityAdd<T>(ref T[] array, T item)
        {
            Array.Resize(ref array, array.Length + 1);
            array[array.Length - 1] = item;
        }

        public ArchetypeChunkComponentType<T> GetArchetypeChunkComponentType<T>(bool isReadOnly = false)
            where T : struct, IComponentData
        {
            AddReaderWriter(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.Create<T>());
            return EntityManager.GetArchetypeChunkComponentType<T>(isReadOnly);
        }

        public ArchetypeChunkBufferType<T> GetArchetypeChunkBufferType<T>(bool isReadOnly = false)
            where T : struct, IBufferElementData
        {
            AddReaderWriter(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.Create<T>());
            return EntityManager.GetArchetypeChunkBufferType<T>(isReadOnly);
        }

        public ArchetypeChunkSharedComponentType<T> GetArchetypeChunkSharedComponentType<T>()
            where T : struct, ISharedComponentData
        {
            AddReaderWriter(ComponentType.ReadOnly<T>());
            return EntityManager.GetArchetypeChunkSharedComponentType<T>();
        }

        public ArchetypeChunkEntityType GetArchetypeChunkEntityType()
        {
            AddReaderWriter(ComponentType.ReadOnly<Entity>());
            return EntityManager.GetArchetypeChunkEntityType();
        }

        public ComponentDataFromEntity<T> GetComponentDataFromEntity<T>(bool isReadOnly = false)
            where T : struct, IComponentData
        {
            AddReaderWriter(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.Create<T>());
            return EntityManager.GetComponentDataFromEntity<T>(isReadOnly);
        }

        internal void AddReaderWriter(ComponentType componentType)
        {
            if (CalculateReaderWriterDependency.Add(componentType, m_JobDependencyForReadingManagers, m_JobDependencyForWritingManagers))
            {
                m_JobDependencyForReadingManagersPtr = (int*)m_JobDependencyForReadingManagers.GetUnsafePtr();
                m_JobDependencyForWritingManagersPtr = (int*)m_JobDependencyForWritingManagers.GetUnsafePtr();

                m_JobDependencyForReadingManagersLength = m_JobDependencyForReadingManagers.Length;
                m_JobDependencyForWritingManagersLength = m_JobDependencyForWritingManagers.Length;
                CompleteDependencyInternal();
            }
        }

        internal ComponentGroup GetComponentGroupInternal(ComponentType* componentTypes, int count)
        {
            for (var i = 0; i != m_ComponentGroups.Length; i++)
            {
                if (m_ComponentGroups[i].CompareComponents(componentTypes, count))
                    return m_ComponentGroups[i];
            }

            var group = EntityManager.CreateComponentGroup(componentTypes, count);
            group.SetFilterChangedRequiredVersion(m_LastSystemVersion);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            group.DisallowDisposing = "ComponentGroup.Dispose() may not be called on a ComponentGroup created with ComponentSystem.GetComponentGroup. The ComponentGroup will automatically be disposed by the ComponentSystem.";
#endif

            ArrayUtilityAdd(ref m_ComponentGroups, group);

            for (int i = 0; i != count; i++)
                AddReaderWriter(componentTypes[i]);

            //@TODO: Shouldn't this sync fence on the newly depent types?

            return group;
        }

        internal ComponentGroup GetComponentGroupInternal(ComponentType[] componentTypes)
        {
            fixed (ComponentType* typesPtr = componentTypes)
            {
                return GetComponentGroupInternal(typesPtr, componentTypes.Length);
            }
        }


        protected ComponentGroup GetComponentGroup(params ComponentType[] componentTypes)
        {
            fixed (ComponentType* typesPtr = componentTypes)
            {
                return GetComponentGroupInternal(typesPtr, componentTypes.Length);
            }
        }

        protected ComponentGroupArray<T> GetEntities<T>() where T : struct
        {
            for (var i = 0; i != m_CachedComponentGroupArrays.Length; i++)
            {
                if (m_CachedComponentGroupArrays[i].CachedType == typeof(T))
                    return new ComponentGroupArray<T>(m_CachedComponentGroupArrays[i]);
            }

            var cache = new ComponentGroupArrayStaticCache(typeof(T), EntityManager, this);
            ArrayUtilityAdd(ref m_CachedComponentGroupArrays, cache);
            return new ComponentGroupArray<T>(cache);
        }

        protected void UpdateInjectedComponentGroups()
        {
            if (null == m_InjectedComponentGroups)
                return;

            ulong gchandle;
            var pinnedSystemPtr = (byte*)UnsafeUtility.PinGCObjectAndGetAddress(this, out gchandle);

            try
            {
                foreach (var group in m_InjectedComponentGroups)
                    group.UpdateInjection(pinnedSystemPtr);

                m_InjectFromEntityData.UpdateInjection(pinnedSystemPtr, EntityManager);
            }
            catch
            {
                UnsafeUtility.ReleaseGCObject(gchandle);
                throw;
            }
            UnsafeUtility.ReleaseGCObject(gchandle);
        }

        internal void CompleteDependencyInternal()
        {
            m_SafetyManager.CompleteDependencies(m_JobDependencyForReadingManagersPtr, m_JobDependencyForReadingManagersLength, m_JobDependencyForWritingManagersPtr, m_JobDependencyForWritingManagersLength);
        }
    }

    public abstract class ComponentSystem : ComponentSystemBase
    {
        EntityCommandBuffer m_DeferredEntities;

        public EntityCommandBuffer PostUpdateCommands => m_DeferredEntities;

        unsafe void BeforeOnUpdate()
        {
            BeforeUpdateVersioning();
            // CompleteDependencyInternal();
            UpdateInjectedComponentGroups();

            m_DeferredEntities = new EntityCommandBuffer(Allocator.Temp);
        }

        void AfterOnUpdate()
        {
            AfterUpdateVersioning();

            // JobHandle.ScheduleBatchedJobs();

            m_DeferredEntities.Playback(EntityManager);
            m_DeferredEntities.Dispose();
        }

        internal sealed override void InternalUpdate()
        {
            if (Enabled && ShouldRunSystem())
            {
                if (!m_PreviouslyEnabled)
                {
                    m_PreviouslyEnabled = true;
                    OnStartRunning();
                }

                BeforeOnUpdate();

                try
                {
                    OnUpdate();
                }
                finally
                {
                    AfterOnUpdate();
                }
            }
            else if (m_PreviouslyEnabled)
            {
                m_PreviouslyEnabled = false;
                OnStopRunning();
            }
        }

        protected sealed override void OnBeforeCreateManagerInternal(World world, int capacity)
        {
            base.OnBeforeCreateManagerInternal(world, capacity);
        }

        protected sealed override void OnBeforeDestroyManagerInternal()
        {
            base.OnBeforeDestroyManagerInternal();
        }

        /// <summary>
        /// Called once per frame on the main thread.
        /// </summary>
        protected abstract void OnUpdate();
    }

    public unsafe abstract class BarrierSystem : ComponentSystem
    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS	
        private List<EntityCommandBuffer> m_PendingBuffers;
#else
        private NativeList<EntityCommandBuffer> m_PendingBuffers;
#endif
        public EntityCommandBuffer CreateCommandBuffer()
        {
            var cmds = new EntityCommandBuffer(Allocator.Temp);

            m_PendingBuffers.Add(cmds);

            return cmds;
        }

        protected override void OnCreateManager(int capacity)
        {
            base.OnCreateManager(capacity);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBuffers = new List<EntityCommandBuffer>();
#else
            m_PendingBuffers = new NativeList<EntityCommandBuffer>(Allocator.Persistent);
#endif
        }

        protected override void OnDestroyManager()
        {
            FlushBuffers(false);

#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBuffers.Dispose();
#endif

            base.OnDestroyManager();
        }

        protected sealed override void OnUpdate()
        {
            FlushBuffers(true);

            m_PendingBuffers.Clear();
        }

        private void FlushBuffers(bool playBack)
        {
            int length = m_PendingBuffers.
#if ENABLE_UNITY_COLLECTIONS_CHECKS	
             Count;
#else
             Length;
#endif
            for (int i = 0; i < length; ++i)
            {
                if (playBack) m_PendingBuffers[i].Playback(EntityManager);
                m_PendingBuffers[i].Dispose();
            }
        }
    }
}