using System;
using System.Text;
using UnityEditor.Graphing;
using UnityEngine;

namespace UnityEditor.ShaderGraph.Internal
{
    [Serializable]
    public abstract class VectorShaderProperty : AbstractShaderProperty<Vector4>
    {
        internal override bool isExposable => true;
        internal override bool isRenamable => true;
        internal virtual int vectorDimension => 4;

        internal override string GetHLSLVariableName(bool isSubgraphProperty)
        {
            HLSLDeclaration decl = GetDefaultHLSLDeclaration();
            if (decl == HLSLDeclaration.HybridPerInstance)
                return $"UNITY_ACCESS_HYBRID_INSTANCED_PROP({referenceName}, {concretePrecision.ToShaderString()}{vectorDimension})";
            else
                return referenceName;
        }

        internal override string GetHLSLVariableName(bool isSubgraphProperty)
        {
            HLSLDeclaration decl = GetDefaultHLSLDeclaration();
            if (decl == HLSLDeclaration.HybridPerInstance)
                return $"UNITY_ACCESS_INSTANCED_PROP(unity_Builtins0, {referenceName})";
            else
                return referenceName;
        }

        internal override string GetPropertyBlockString()
        {
            return $"{hideTagString}{referenceName}(\"{displayName}\", Vector) = ({NodeUtils.FloatToShaderValueShaderLabSafe(value.x)}, {NodeUtils.FloatToShaderValueShaderLabSafe(value.y)}, {NodeUtils.FloatToShaderValueShaderLabSafe(value.z)}, {NodeUtils.FloatToShaderValueShaderLabSafe(value.w)})";
        }

        internal override string GetPropertyAsArgumentString(string precisionString)
        {
            return $"{concreteShaderValueType.ToShaderString(precisionString)} {referenceName}";
        }
    }
}
