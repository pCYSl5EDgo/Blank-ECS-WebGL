using UnityEngine;
using UnityEngine.Rendering;

using Unity.Entities;
using Unity.Transforms;
using Unity.Rendering;

public class Manager : MonoBehaviour
{
    [SerializeField] Mesh mesh;
    [SerializeField] Material material;
    [SerializeField] Texture texture;
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
                mainTexture = texture,
            },
            mesh = mesh,
            subMesh = 0,
        };
        PlayerLoopManager.RegisterDomainUnload(DestroyAll, 10000);
        var world = World.Active = new World("x");
        World.Active.CreateManager(typeof(EndFrameTransformSystem));
        World.Active.CreateManager(typeof(CountUpSystem), GameObject.Find("Count").GetComponent<TMPro.TMP_Text>());
        World.Active.CreateManager<MeshInstanceRendererSystem>().ActiveCamera = GetComponent<Camera>();
        ScriptBehaviourUpdateOrder.UpdatePlayerLoop(world);

        manager = world.GetExistingManager<EntityManager>();
        archetype = manager.CreateArchetype(ComponentType.Create<Position>(), ComponentType.Create<MeshInstanceRenderer>(), ComponentType.Create<Static>());
    }

    Matrix4x4[] matrices = new Matrix4x4[1]{
        Matrix4x4.identity
    };

    // Update is called once per frame
    void Update()
    {
        // Graphics.DrawMesh(render.mesh, matrices[0], render.material, 0, null, 0, null, render.castShadows, render.receiveShadows, null, false);
        // Graphics.DrawMeshInstanced(render.mesh, 0, render.material, matrices, matrices.Length, null, render.castShadows, render.receiveShadows, 0, null, LightProbeUsage.Off, null);
        if (!Input.GetMouseButton(0)) return;
        var e = manager.CreateEntity(archetype);
        manager.SetComponentData(e, new Position
        {
            Value = new Unity.Mathematics.float3((Random.value - 0.5f) * 10, (Random.value - 0.5f) * 10, (Random.value) * 10)
        });
        manager.SetSharedComponentData(e, render);
    }

    static void DestroyAll()
    {
        World.DisposeAllWorlds();
        ScriptBehaviourUpdateOrder.UpdatePlayerLoop();
    }
}