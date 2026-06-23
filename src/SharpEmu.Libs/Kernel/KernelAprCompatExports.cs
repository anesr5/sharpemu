// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

public static class KernelAprCompatExports
{
    private static readonly ConcurrentDictionary<uint, ulong> _submittedCommandBuffers = new();
    private static int _nextSubmissionId;

    [SysAbiExport(
        Nid = "ASoW5WE-UPo",
        ExportName = "sceKernelAprSubmitCommandBufferAndGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBufferAndGetResult(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var priority = ctx[CpuRegister.Rsi];
        var resultAddress = ctx[CpuRegister.Rdx];
        var outSubmissionId = ctx[CpuRegister.Rcx];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        if (submissionId == 0)
        {
            submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        }

        _submittedCommandBuffers[submissionId] = commandBuffer;

        var completionResult = AmprExports.CompleteCommandBuffer(ctx, commandBuffer);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }

        if (outSubmissionId != 0 && !TryWriteUInt32(ctx, outSubmissionId, submissionId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (resultAddress != 0 && !TryWriteAprResult(ctx, resultAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceApr(ctx, "submit_get_result", submissionId, commandBuffer, priority, resultAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "rqwFKI4PAiM",
        ExportName = "sceKernelAprWaitCommandBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprWaitCommandBuffer(CpuContext ctx)
    {
        var submissionId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var priority = ctx[CpuRegister.Rsi];
        var resultAddress = ctx[CpuRegister.Rdx];

        if (!_submittedCommandBuffers.TryRemove(submissionId, out var commandBuffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (resultAddress != 0 && !TryWriteAprResult(ctx, resultAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceApr(ctx, "wait", submissionId, commandBuffer, priority, resultAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eE4Szl8sil8",
        ExportName = "sceKernelAprSubmitCommandBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        _submittedCommandBuffers[submissionId] = commandBuffer;

        var completionResult = AmprExports.CompleteCommandBuffer(ctx, commandBuffer);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }

        TraceApr(ctx, "submit", submissionId, commandBuffer, ctx[CpuRegister.Rsi], 0);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qvMUCyyaCSI",
        ExportName = "sceKernelAprSubmitCommandBufferAndGetId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBufferAndGetId(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var outSubmissionId = ctx[CpuRegister.Rdx];
        if (commandBuffer == 0 || outSubmissionId == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        _submittedCommandBuffers[submissionId] = commandBuffer;

        var completionResult = AmprExports.CompleteCommandBuffer(ctx, commandBuffer);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }

        if (!TryWriteUInt32(ctx, outSubmissionId, submissionId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceApr(ctx, "submit_get_id", submissionId, commandBuffer, ctx[CpuRegister.Rsi], outSubmissionId);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryWriteAprResult(CpuContext ctx, ulong resultAddress)
    {
        Span<byte> result = stackalloc byte[sizeof(ulong)];
        result.Clear();
        return ctx.Memory.TryWrite(resultAddress, result);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static void TraceApr(
        CpuContext ctx,
        string operation,
        uint submissionId,
        ulong commandBuffer,
        ulong priority,
        ulong aux)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] apr.{operation}: id=0x{submissionId:X8} cmd=0x{commandBuffer:X16} priority=0x{priority:X16} aux=0x{aux:X16} ret=0x{returnRip:X16}");
        if (aux != 0 &&
            ctx.TryReadUInt64(aux, out var result0) &&
            ctx.TryReadUInt64(aux + sizeof(ulong), out var result1))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] apr.{operation}.result: addr=0x{aux:X16} q0=0x{result0:X16} q1=0x{result1:X16}");
        }
    }
}
