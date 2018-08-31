using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using Unity.Entities;
using Unity.Transforms;

using TMPro;

sealed class CountUpSystem : ComponentSystem
{
    public CountUpSystem(TMP_Text text)
    {
        this.text = text;
    }
    ComponentGroup g;
    uint cachedCount = 0;
    TMP_Text text;
    protected override void OnCreateManager(int capacity)
    {
        g = GetComponentGroup(ComponentType.ReadOnly<Position>());
    }
    protected override void OnUpdate()
    {
        var count = (uint)g.CalculateLength();
        if (cachedCount == count) return;
        text.text = (cachedCount = count).ToString();
    }
}
