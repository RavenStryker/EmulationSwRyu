using ChocolArm64.Decoder;
using ChocolArm64.Events;
using ChocolArm64.Instruction;
using ChocolArm64.Memory;
using ChocolArm64.State;
using ChocolArm64.Translation;
using System;
using System.Collections.Concurrent;
using System.Reflection.Emit;

namespace ChocolArm64
{
    public class ATranslator
    {
        private ConcurrentDictionary<long, ATranslatedSub> CachedSubs;

        public event EventHandler<ACpuTraceEventArgs> CpuTrace;

        public bool EnableCpuTrace { get; set; }

        public ATranslator()
        {
            CachedSubs = new ConcurrentDictionary<long, ATranslatedSub>();
        }

        internal void ExecuteSubroutine(AThread Thread, long Position)
        {
            //TODO: Both the execute A32/A64 methods should be merged on the future,
            //when both ISAs are implemented with the interpreter and JIT.
            //As of now, A32 only has a interpreter and A64 a JIT.
            AThreadState State  = Thread.ThreadState;
            AMemory      Memory = Thread.Memory;

            if (State.ExecutionMode == AExecutionMode.AArch32)
            {
                ExecuteSubroutineA32(State, Memory);
            }
            else
            {
                ExecuteSubroutineA64(State, Memory, Position);
            }
        }

        private void ExecuteSubroutineA32(AThreadState State, AMemory Memory)
        {
            do
            {
                AOpCode OpCode = ADecoder.DecodeOpCode(State, Memory, State.R15);

                OpCode.Interpreter(State, Memory, OpCode);
            }
            while (State.R15 != 0 && State.Running);
        }

        private void ExecuteSubroutineA64(AThreadState State, AMemory Memory, long Position)
        {
            do
            {
                if (EnableCpuTrace)
                {
                    CpuTrace?.Invoke(this, new ACpuTraceEventArgs(Position));
                }

                if (!CachedSubs.TryGetValue(Position, out ATranslatedSub Sub))
                {
                    Sub = TranslateTier0(State, Memory, Position);
                }

                if (Sub.ShouldReJit())
                {
                    TranslateTier1(State, Memory, Position);
                }

                Position = Sub.Execute(State, Memory);
            }
            while (Position != 0 && State.Running);
        }

        internal bool TryGetCachedSub(AOpCode OpCode, out ATranslatedSub Sub)
        {
            if (OpCode.Emitter != AInstEmit.Bl)
            {
                Sub = null;

                return false;
            }

            return TryGetCachedSub(((AOpCodeBImmAl)OpCode).Imm, out Sub);
        }

        internal bool TryGetCachedSub(long Position, out ATranslatedSub Sub)
        {
            return CachedSubs.TryGetValue(Position, out Sub);
        }

        internal bool HasCachedSub(long Position)
        {
            return CachedSubs.ContainsKey(Position);
        }

        private ATranslatedSub TranslateTier0(AThreadState State, AMemory Memory, long Position)
        {
            ABlock Block = ADecoder.DecodeBasicBlock(State, this, Memory, Position);

            ABlock[] Graph = new ABlock[] { Block };

            string SubName = GetSubName(Position);

            AILEmitterCtx Context = new AILEmitterCtx(this, Graph, Block, SubName);

            do
            {
                Context.EmitOpCode();
            }
            while (Context.AdvanceOpCode());

            ATranslatedSub Subroutine = Context.GetSubroutine();

            Subroutine.SetType(ATranslatedSubType.SubTier0);

            CachedSubs.AddOrUpdate(Position, Subroutine, (Key, OldVal) => Subroutine);

            AOpCode LastOp = Block.GetLastOp();

            return Subroutine;
        }

        private void TranslateTier1(AThreadState State, AMemory Memory, long Position)
        {
            (ABlock[] Graph, ABlock Root) Cfg = ADecoder.DecodeSubroutine(State, this, Memory, Position);

            string SubName = GetSubName(Position);

            AILEmitterCtx Context = new AILEmitterCtx(this, Cfg.Graph, Cfg.Root, SubName);

            if (Context.CurrBlock.Position != Position)
            {
                Context.Emit(OpCodes.Br, Context.GetLabel(Position));
            }

            do
            {
                Context.EmitOpCode();
            }
            while (Context.AdvanceOpCode());

            //Mark all methods that calls this method for ReJiting,
            //since we can now call it directly which is faster.
            if (CachedSubs.TryGetValue(Position, out ATranslatedSub OldSub))
            {
                foreach (long CallerPos in OldSub.GetCallerPositions())
                {
                    if (CachedSubs.TryGetValue(Position, out ATranslatedSub CallerSub))
                    {
                        CallerSub.MarkForReJit();
                    }
                }
            }

            ATranslatedSub Subroutine = Context.GetSubroutine();

            Subroutine.SetType(ATranslatedSubType.SubTier1);

            CachedSubs.AddOrUpdate(Position, Subroutine, (Key, OldVal) => Subroutine);
        }

        private string GetSubName(long Position)
        {
            return $"Sub{Position:x16}";
        }
    }
}