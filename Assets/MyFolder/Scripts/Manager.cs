using UnityEngine;
using UnityEngine.Rendering;

using Unity.Entities;
using Unity.Transforms;
using Unity.Rendering;

public class Manager : MonoBehaviour
{
    [SerializeField] Mesh mesh;
    [SerializeField] Material material;
    MeshInstanceRenderer render;
    EntityArchetype archetype;
    EntityManager manager;
    // Use this for initialization
    void Start()
    {
        render = new MeshInstanceRenderer
        {
            castShadows = ShadowCastingMode.On,
            receiveShadows = true,
            material = new Material(material)
            {
                enableInstancing = true,
            },
            mesh = mesh,
            subMesh = 0,
        };
        var world = World.Active = new World("x");
        World.Active.CreateManager(typeof(EndFrameTransformSystem));
        World.Active.CreateManager<MeshInstanceRendererSystem>().ActiveCamera = GetComponent<Camera>();
        ScriptBehaviourUpdateOrder.UpdatePlayerLoop(world);

        manager = world.GetExistingManager<EntityManager>();
        archetype = manager.CreateArchetype(ComponentType.Create<Position>(), ComponentType.Create<MeshInstanceRenderer>());
    }

    // Update is called once per frame
    void Update()
    {
        if (!Input.GetMouseButton(0)) return;
        var e = manager.CreateEntity(archetype);
        manager.SetComponentData(e, new Position
        {
            Value = new Unity.Mathematics.float3((Random.value - 0.5f) * 10, (Random.value - 0.5f) * 10, (Random.value) * 10)
        });
		manager.SetSharedComponentData(e, render);
    }
}
