using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
#if !UNITY_2018_2_OR_NEWER
using UnityEditor.Experimental.Build.Player;
#else
using UnityEditor.Build.Player;
#endif

namespace Unity.Entities.Editor
{
    [InitializeOnLoad]
    public sealed class ExtraTypesProvider
    {
        const string k_AssemblyName = "Unity.Entities";

        static ExtraTypesProvider()
        {
            //@TODO: Only produce JobProcessComponentDataExtensions.JobStruct_Process1
            //       if there is any use of that specific type in deployed code.

            PlayerBuildInterface.ExtraTypesProvider += () => new HashSet<string>();
        }
    }
}