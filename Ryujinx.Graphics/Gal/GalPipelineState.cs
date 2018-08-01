﻿namespace Ryujinx.Graphics.Gal
{
    public struct GalVertexBinding
    {
        //VboKey shouldn't be here, but ARB_vertex_attrib_binding is core since 4.3

        public bool Enabled;
        public int Stride;
        public long VboKey;
        public GalVertexAttrib[] Attribs;
    }

    public struct GalConstBufferBinding
    {
        public bool Enabled;
        public long Key;
    }

    public struct GalPipelineState
    {
        public const int ConstBuffersPerStage = 18;

        public GalConstBufferBinding[][] ConstBufferKeys;

        public GalVertexBinding[] VertexBindings;

        public GalFrontFace FrontFace;

        public bool CullFaceEnabled;
        public GalCullFace CullFace;

        public bool DepthTestEnabled;
        public float DepthClear;
        public GalComparisonOp DepthFunc;

        public bool StencilTestEnabled;
        public int StencilClear;

        public GalComparisonOp StencilBackFuncFunc;
        public int StencilBackFuncRef;
        public uint StencilBackFuncMask;
        public GalStencilOp StencilBackOpFail;
        public GalStencilOp StencilBackOpZFail;
        public GalStencilOp StencilBackOpZPass;
        public uint StencilBackMask;

        public GalComparisonOp StencilFrontFuncFunc;
        public int StencilFrontFuncRef;
        public uint StencilFrontFuncMask;
        public GalStencilOp StencilFrontOpFail;
        public GalStencilOp StencilFrontOpZFail;
        public GalStencilOp StencilFrontOpZPass;
        public uint StencilFrontMask;

        public bool BlendEnabled;
        public bool BlendSeparateAlpha;
        public GalBlendEquation BlendEquationRgb;
        public GalBlendFactor BlendFuncSrcRgb;
        public GalBlendFactor BlendFuncDstRgb;
        public GalBlendEquation BlendEquationAlpha;
        public GalBlendFactor BlendFuncSrcAlpha;
        public GalBlendFactor BlendFuncDstAlpha;

        public bool PrimitiveRestartEnabled;
        public uint PrimitiveRestartIndex;
    }
}