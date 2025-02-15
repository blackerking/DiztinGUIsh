﻿using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;

namespace Diz.Cpu._65816;

public class Cpu65C816<TByteSource> : Cpu<TByteSource> 
    where TByteSource : 
    IRomByteFlagsGettable, 
    IRomByteFlagsSettable, 
    ISnesAddressConverter, 
    ISteppable, 
    IReadOnlyByteSource, 
    ISnesIntermediateAddress,
    IInOutPointSettable,
    IInOutPointGettable,
    IReadOnlyLabels
{
    public override int Step(TByteSource data, int offset, bool branch, bool force, int prevOffset = -1)
    {
        var (opcode, directPage, dataBank, xFlag, mFlag) = GetCpuStateFor(data, offset, prevOffset);
        var length = MarkAsOpcodeAt(data, offset, dataBank, directPage, xFlag, mFlag);
        MarkInOutPoints(data, offset);

        var nextOffset = offset + length;

        var useIndirectAddress = 
            opcode is 0x4C or 0x5C or 0x80 or 0x82 || 
            branch && opcode is 0x10 or 0x30 or 0x50 or 0x70 or 0x90 or 0xB0 or 0xD0 or 0xF0 or 0x20 or 0x22;

        if (force || !useIndirectAddress) 
            return nextOffset;
            
        var iaNextOffsetPc = data.ConvertSnesToPc(GetIntermediateAddress(data, offset, true));
        if (iaNextOffsetPc >= 0)
            return iaNextOffsetPc;

        return nextOffset;
    }

    private int MarkAsOpcodeAt(TByteSource data, int offset, int dataBank, int directPage, bool xFlag, bool mFlag)
    {
        var numBytesToChange = 1;
        for (var i = 0; i < numBytesToChange; ++i)
        {
            var flagType = i == 0 ? FlagType.Opcode : FlagType.Operand;
            data.SetFlag(offset + i, flagType);
            data.SetDataBank(offset + i, dataBank);
            data.SetDirectPage(offset + i, directPage);
            data.SetXFlag(offset + i, xFlag);
            data.SetMFlag(offset + i, mFlag);
                
            if (i == 0) 
                numBytesToChange = GetInstructionLength(data, offset);
        }

        return numBytesToChange;
    }

    private static (byte? opcode, int directPage, int dataBank, bool xFlag, bool mFlag) 
        GetCpuStateFor(TByteSource data, int offset, int prevOffset)
    {
        int directPage, dataBank;
        bool xFlag, mFlag;
        byte? opcode;

        void SetCpuStateFromCurrentOffset()
        {
            opcode = data.GetRomByte(offset);
            (directPage, dataBank, xFlag, mFlag) = GetCpuStateAt(data, offset);
        }

        void SetCpuStateFromPreviousOffset()
        {
            // go backwards from previous offset if it's valid but not an opcode
            while (prevOffset >= 0 && data.GetFlag(prevOffset) == FlagType.Operand)
                prevOffset--;

            // if we didn't land on an opcode, forget it
            if (prevOffset < 0 || data.GetFlag(prevOffset) != FlagType.Opcode) 
                return;
                
            // set these values to the PREVIOUS instruction
            (directPage, dataBank, xFlag, mFlag) = GetCpuStateAt(data, prevOffset);
        }
            
        void SetMxFlagsFromRepSepAtOffset()
        {
            if (opcode != 0xC2 && opcode != 0xE2) // REP SEP 
                return;

            var operand = data.GetRomByte(offset + 1);
                
            xFlag = (operand & 0x10) != 0 ? opcode == 0xE2 : xFlag;
            mFlag = (operand & 0x20) != 0 ? opcode == 0xE2 : mFlag;
        }
            
        SetCpuStateFromCurrentOffset();     // set from our current position first
        SetCpuStateFromPreviousOffset();    // if available, set from the previous offset instead.
        SetMxFlagsFromRepSepAtOffset();

        return (opcode, directPage, dataBank, xFlag, mFlag);
    }

    private static (int directPage, int dataBank, bool xFlag, bool mFlag) 
        GetCpuStateAt(TByteSource data, int offset)
    {
        return (
            data.GetDirectPage(offset), 
            data.GetDataBank(offset), 
            data.GetXFlag(offset), 
            data.GetMFlag(offset)
        );
    }

    // input: ROM offset
    // return: a SNES address
    public override int GetIntermediateAddress(TByteSource data, int offset, bool resolve)
    {
        int bank;
        int programCounter;
            
        #if !DIZ_3_BRANCH
        // old way
        var opcode = data.GetRomByte(offset);
        #else
            // new way
            var byteEntry = GetByteEntryRom(data, offset);
            var opcode = byteEntry?.Byte;
        #endif
            
        if (opcode == null)
            return -1;

        var mode = GetAddressMode(data, offset);
        switch (mode)
        {
            case Cpu65C816Constants.AddressMode.DirectPage:
            case Cpu65C816Constants.AddressMode.DirectPageXIndex:
            case Cpu65C816Constants.AddressMode.DirectPageYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageXIndexIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageIndirectYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirectYIndex:
                if (resolve)
                {
                    var directPage = data.GetDirectPage(offset);
                    var operand = data.GetRomByte(offset + 1);
                    if (!operand.HasValue)
                        return -1;
                    return (directPage + (int)operand) & 0xFFFF;
                }
                else
                {
                    goto case Cpu65C816Constants.AddressMode.DirectPageSIndex;
                }
            case Cpu65C816Constants.AddressMode.DirectPageSIndex:
            case Cpu65C816Constants.AddressMode.DirectPageSIndexIndirectYIndex:
                return data.GetRomByte(offset + 1) ?? -1;
            case Cpu65C816Constants.AddressMode.Address:
            case Cpu65C816Constants.AddressMode.AddressXIndex:
            case Cpu65C816Constants.AddressMode.AddressYIndex:
            case Cpu65C816Constants.AddressMode.AddressXIndexIndirect:
            {
                bank = opcode is 0x20 or 0x4C or 0x7C or 0xFC
                    ? data.ConvertPCtoSnes(offset) >> 16
                    : data.GetDataBank(offset);
                var operand = data.GetRomWord(offset + 1);
                if (!operand.HasValue)
                    return -1;
                    
                return (bank << 16) | (int)operand;
            }
            case Cpu65C816Constants.AddressMode.AddressIndirect:
            case Cpu65C816Constants.AddressMode.AddressLongIndirect:
            {
                var operand = data.GetRomWord(offset + 1) ?? -1;
                return operand;
            }
            case Cpu65C816Constants.AddressMode.Long:
            case Cpu65C816Constants.AddressMode.LongXIndex:
            {
                var operand = data.GetRomLong(offset + 1) ?? -1;
                return operand;
            }
            case Cpu65C816Constants.AddressMode.Relative8:
            {
                programCounter = data.ConvertPCtoSnes(offset + 2);
                bank = programCounter >> 16;
                var romByte = data.GetRomByte(offset + 1);
                if (!romByte.HasValue)
                    return -1;
                    
                return (bank << 16) | ((programCounter + (sbyte)romByte) & 0xFFFF);
            }
            case Cpu65C816Constants.AddressMode.Relative16:
            {
                programCounter = data.ConvertPCtoSnes(offset + 3);
                bank = programCounter >> 16;
                var romByte = data.GetRomWord(offset + 1);
                if (!romByte.HasValue)
                    return -1;
                    
                return (bank << 16) | ((programCounter + (short)romByte) & 0xFFFF);
            }
        }
        return -1;
    }

    public override string GetInstruction(TByteSource data, int offset)
    {
        var mode = GetAddressMode(data, offset);
        if (mode == null)
            throw new InvalidDataException("Expected non-null mode");
            
        var format = GetInstructionFormatString(data, offset);
        var mnemonic = GetMnemonic(data, offset);
            
        int numDigits1 = 0, numDigits2 = 0;
        int? value1 = null, value2 = null;
        var identified = false;
            
        switch (mode)
        {
            case Cpu65C816Constants.AddressMode.BlockMove:
                identified = true;
                numDigits1 = numDigits2 = 2;
                value1 = data.GetRomByte(offset + 1);
                value2 = data.GetRomByte(offset + 2);
                break;
            case Cpu65C816Constants.AddressMode.Constant8:
            case Cpu65C816Constants.AddressMode.Immediate8:
                identified = true;
                numDigits1 = 2;
                value1 = data.GetRomByte(offset + 1);
                break;
            case Cpu65C816Constants.AddressMode.Immediate16:
                identified = true;
                numDigits1 = 4;
                value1 = data.GetRomWord(offset + 1);
                break;
        }

        string op1, op2 = "";
        if (identified)
        {
            op1 = CreateHexStr(value1, numDigits1);
            op2 = CreateHexStr(value2, numDigits2);
        }
        else
        {
            // dom note: this is where we could inject expressions if needed. it gives stuff like "$F001".
            // we could substitute our expression of "$#F000 + $#01" or "some_struct.member" like "player.hp"
            // the expression must be verified to always match the bytes in the file [unless we allow overriding]
            op1 = FormatOperandAddress(data, offset, mode.Value);
        }
            
        return string.Format(format, mnemonic, op1, op2);
    }

    public override int AutoStepSafe(TByteSource byteSource, int offset)
    {
        var cmd = new AutoStepper65816<TByteSource>(byteSource);
        cmd.Run(offset);
        return cmd.Offset;
    }

    private static string CreateHexStr(int? v, int numDigits)
    {
        if (numDigits == 0)
            return "";

        if (v == null)
            throw new InvalidDataException("Expected non-null input value, got null");
            
        return Util.NumberToBaseString((int) v, Util.NumberBase.Hexadecimal, numDigits, true);
    }

    public override int GetInstructionLength(TByteSource data, int offset)
    {
        var mode = GetAddressMode(data, offset);
            
        // not sure if this is the right thing. probably fine. if we hit this, we're in a weird mess anyway.
        return mode == null ? 1 : GetInstructionLength(mode.Value);
    }

    public override void MarkInOutPoints(TByteSource data, int offset)
    {
        var opcode = data.GetRomByte(offset);
        var iaOffsetPc = data.ConvertSnesToPc(data.GetIntermediateAddress(offset, true));

        // set read point on EA
        if (iaOffsetPc >= 0 && ( // these are all read/write/math instructions
                ((opcode & 0x04) != 0) || ((opcode & 0x0F) == 0x01) || ((opcode & 0x0F) == 0x03) ||
                ((opcode & 0x1F) == 0x12) || ((opcode & 0x1F) == 0x19)) &&
            (opcode != 0x45) && (opcode != 0x55) && (opcode != 0xF5) && (opcode != 0x4C) &&
            (opcode != 0x5C) && (opcode != 0x6C) && (opcode != 0x7C) && (opcode != 0xDC) && (opcode != 0xFC)
           ) data.SetInOutPoint(iaOffsetPc, InOutPoint.ReadPoint);

        // set end point on offset
        if (opcode == 0x40 || opcode == 0x4C || opcode == 0x5C || opcode == 0x60 // RTI JMP JML RTS
            || opcode == 0x6B || opcode == 0x6C || opcode == 0x7C || opcode == 0x80 // RTL JMP JMP BRA
            || opcode == 0x82 || opcode == 0xDB || opcode == 0xDC // BRL STP JML
           ) data.SetInOutPoint(offset, InOutPoint.EndPoint);

        // set out point on offset
        // set in point on EA
        if (iaOffsetPc >= 0 && (
                opcode == 0x4C || opcode == 0x5C || opcode == 0x80 || opcode == 0x82 // JMP JML BRA BRL
                || opcode == 0x10 || opcode == 0x30 || opcode == 0x50 || opcode == 0x70  // BPL BMI BVC BVS
                || opcode == 0x90 || opcode == 0xB0 || opcode == 0xD0 || opcode == 0xF0  // BCC BCS BNE BEQ
                || opcode == 0x20 || opcode == 0x22)) // JSR JSL
        {
            data.SetInOutPoint(offset, InOutPoint.OutPoint);
            data.SetInOutPoint(iaOffsetPc, InOutPoint.InPoint);
        }
    }

    private static int GetInstructionLength(Cpu65C816Constants.AddressMode mode)
    {
        switch (mode)
        {
            case Cpu65C816Constants.AddressMode.Implied:
            case Cpu65C816Constants.AddressMode.Accumulator:
                return 1;
            case Cpu65C816Constants.AddressMode.Constant8:
            case Cpu65C816Constants.AddressMode.Immediate8:
            case Cpu65C816Constants.AddressMode.DirectPage:
            case Cpu65C816Constants.AddressMode.DirectPageXIndex:
            case Cpu65C816Constants.AddressMode.DirectPageYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageSIndex:
            case Cpu65C816Constants.AddressMode.DirectPageIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageXIndexIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageIndirectYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageSIndexIndirectYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirectYIndex:
            case Cpu65C816Constants.AddressMode.Relative8:
                return 2;
            case Cpu65C816Constants.AddressMode.Immediate16:
            case Cpu65C816Constants.AddressMode.Address:
            case Cpu65C816Constants.AddressMode.AddressXIndex:
            case Cpu65C816Constants.AddressMode.AddressYIndex:
            case Cpu65C816Constants.AddressMode.AddressIndirect:
            case Cpu65C816Constants.AddressMode.AddressXIndexIndirect:
            case Cpu65C816Constants.AddressMode.AddressLongIndirect:
            case Cpu65C816Constants.AddressMode.BlockMove:
            case Cpu65C816Constants.AddressMode.Relative16:
                return 3;
            case Cpu65C816Constants.AddressMode.Long:
            case Cpu65C816Constants.AddressMode.LongXIndex:
                return 4;
            default:
                return 1;
        }
    }

    private string FormatOperandAddress(TByteSource data, int offset, Cpu65C816Constants.AddressMode mode)
    {
        var address = data.GetIntermediateAddress(offset);
        if (address < 0) 
            return "";

        if (data is IReadOnlyLabels labelProvider)
        {
            var label = labelProvider.Labels.GetLabelName(address);
            if (label != "") 
                return label;   
        }

        var count = BytesToShow(mode);
        if (mode is Cpu65C816Constants.AddressMode.Relative8 or Cpu65C816Constants.AddressMode.Relative16)
        {
            var romWord = data.GetRomWord(offset + 1);
            if (!romWord.HasValue)
                return "";
                
            address = (int)romWord;
        }
            
        address &= ~(-1 << (8 * count));
        return Util.NumberToBaseString(address, Util.NumberBase.Hexadecimal, 2 * count, true);
    }

    private string GetMnemonic(TByteSource data, int offset, bool showHint = true)
    {
        var mn = Cpu65C816Constants.Mnemonics[data.GetRomByteUnsafe(offset)];
        if (!showHint) 
            return mn;

        var mode = GetAddressMode(data, offset);
        if (mode == null)
            return mn;
                
        var count = BytesToShow(mode.Value);

        if (mode is Cpu65C816Constants.AddressMode.Constant8 or Cpu65C816Constants.AddressMode.Relative16 or Cpu65C816Constants.AddressMode.Relative8) 
            return mn;

        return count switch
        {
            1 => mn + ".B",
            2 => mn + ".W",
            3 => mn + ".L",
            _ => mn
        };
    }

    private static int BytesToShow(Cpu65C816Constants.AddressMode mode)
    {
        switch (mode)
        {
            case Cpu65C816Constants.AddressMode.Constant8:
            case Cpu65C816Constants.AddressMode.Immediate8:
            case Cpu65C816Constants.AddressMode.DirectPage:
            case Cpu65C816Constants.AddressMode.DirectPageXIndex:
            case Cpu65C816Constants.AddressMode.DirectPageYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageSIndex:
            case Cpu65C816Constants.AddressMode.DirectPageIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageXIndexIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageIndirectYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageSIndexIndirectYIndex:
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirect:
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirectYIndex:
            case Cpu65C816Constants.AddressMode.Relative8:
                return 1;
            case Cpu65C816Constants.AddressMode.Immediate16:
            case Cpu65C816Constants.AddressMode.Address:
            case Cpu65C816Constants.AddressMode.AddressXIndex:
            case Cpu65C816Constants.AddressMode.AddressYIndex:
            case Cpu65C816Constants.AddressMode.AddressIndirect:
            case Cpu65C816Constants.AddressMode.AddressXIndexIndirect:
            case Cpu65C816Constants.AddressMode.AddressLongIndirect:
            case Cpu65C816Constants.AddressMode.Relative16:
                return 2;
            case Cpu65C816Constants.AddressMode.Long:
            case Cpu65C816Constants.AddressMode.LongXIndex:
                return 3;
        }
        return 0;
    }

    // {0} = mnemonic
    // {1} = intermediate address / label OR operand 1 for block move
    // {2} = operand 2 for block move
    private string GetInstructionFormatString(TByteSource data, int offset)
    {
        var mode = GetAddressMode(data, offset);
        switch (mode)
        {
            case Cpu65C816Constants.AddressMode.Implied:
                return "{0}";
            case Cpu65C816Constants.AddressMode.Accumulator:
                return "{0} A";
            case Cpu65C816Constants.AddressMode.Constant8:
            case Cpu65C816Constants.AddressMode.Immediate8:
            case Cpu65C816Constants.AddressMode.Immediate16:
                return "{0} #{1}";
            case Cpu65C816Constants.AddressMode.DirectPage:
            case Cpu65C816Constants.AddressMode.Address:
            case Cpu65C816Constants.AddressMode.Long:
            case Cpu65C816Constants.AddressMode.Relative8:
            case Cpu65C816Constants.AddressMode.Relative16:
                return "{0} {1}";
            case Cpu65C816Constants.AddressMode.DirectPageXIndex:
            case Cpu65C816Constants.AddressMode.AddressXIndex:
            case Cpu65C816Constants.AddressMode.LongXIndex:
                return "{0} {1},X";
            case Cpu65C816Constants.AddressMode.DirectPageYIndex:
            case Cpu65C816Constants.AddressMode.AddressYIndex:
                return "{0} {1},Y";
            case Cpu65C816Constants.AddressMode.DirectPageSIndex:
                return "{0} {1},S";
            case Cpu65C816Constants.AddressMode.DirectPageIndirect:
            case Cpu65C816Constants.AddressMode.AddressIndirect:
                return "{0} ({1})";
            case Cpu65C816Constants.AddressMode.DirectPageXIndexIndirect:
            case Cpu65C816Constants.AddressMode.AddressXIndexIndirect:
                return "{0} ({1},X)";
            case Cpu65C816Constants.AddressMode.DirectPageIndirectYIndex:
                return "{0} ({1}),Y";
            case Cpu65C816Constants.AddressMode.DirectPageSIndexIndirectYIndex:
                return "{0} ({1},S),Y";
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirect:
            case Cpu65C816Constants.AddressMode.AddressLongIndirect:
                return "{0} [{1}]";
            case Cpu65C816Constants.AddressMode.DirectPageLongIndirectYIndex:
                return "{0} [{1}],Y";
            case Cpu65C816Constants.AddressMode.BlockMove:
                return "{0} {1},{2}";
        }
        return "";
    }
        
    public static Cpu65C816Constants.AddressMode? GetAddressMode(TByteSource data, int offset)
    {
        var opcode = data.GetRomByte(offset);
        if (!opcode.HasValue)
            return null;
            
        var mFlag = data.GetMFlag(offset);
        var xFlag = data.GetXFlag(offset);
            
        return GetAddressMode(opcode.Value, mFlag, xFlag);
    }

    public static Cpu65C816Constants.AddressMode GetAddressMode(int opcode, bool mFlag, bool xFlag)
    {
        var mode = Cpu65C816Constants.AddressingModes[opcode];
        return mode switch
        {
            Cpu65C816Constants.AddressMode.ImmediateMFlagDependent => mFlag
                ? Cpu65C816Constants.AddressMode.Immediate8
                : Cpu65C816Constants.AddressMode.Immediate16,
            Cpu65C816Constants.AddressMode.ImmediateXFlagDependent => xFlag
                ? Cpu65C816Constants.AddressMode.Immediate8
                : Cpu65C816Constants.AddressMode.Immediate16,
            _ => mode
        };
    }
}

public static class Cpu65C816Constants
{
    public enum AddressMode : byte
    {
        Implied, Accumulator, Constant8, Immediate8, Immediate16,
        ImmediateXFlagDependent, ImmediateMFlagDependent,
        DirectPage, DirectPageXIndex, DirectPageYIndex,
        DirectPageSIndex, DirectPageIndirect, DirectPageXIndexIndirect,
        DirectPageIndirectYIndex, DirectPageSIndexIndirectYIndex,
        DirectPageLongIndirect, DirectPageLongIndirectYIndex,
        Address, AddressXIndex, AddressYIndex, AddressIndirect,
        AddressXIndexIndirect, AddressLongIndirect,
        Long, LongXIndex, BlockMove, Relative8, Relative16
    }

    public static readonly string[] Mnemonics =
    {
        "BRK", "ORA", "COP", "ORA", "TSB", "ORA", "ASL", "ORA", "PHP", "ORA", "ASL", "PHD", "TSB", "ORA", "ASL", "ORA",
        "BPL", "ORA", "ORA", "ORA", "TRB", "ORA", "ASL", "ORA", "CLC", "ORA", "INC", "TCS", "TRB", "ORA", "ASL", "ORA",
        "JSR", "AND", "JSL", "AND", "BIT", "AND", "ROL", "AND", "PLP", "AND", "ROL", "PLD", "BIT", "AND", "ROL", "AND",
        "BMI", "AND", "AND", "AND", "BIT", "AND", "ROL", "AND", "SEC", "AND", "DEC", "TSC", "BIT", "AND", "ROL", "AND",
        "RTI", "EOR", "WDM", "EOR", "MVP", "EOR", "LSR", "EOR", "PHA", "EOR", "LSR", "PHK", "JMP", "EOR", "LSR", "EOR",
        "BVC", "EOR", "EOR", "EOR", "MVN", "EOR", "LSR", "EOR", "CLI", "EOR", "PHY", "TCD", "JML", "EOR", "LSR", "EOR",
        "RTS", "ADC", "PER", "ADC", "STZ", "ADC", "ROR", "ADC", "PLA", "ADC", "ROR", "RTL", "JMP", "ADC", "ROR", "ADC",
        "BVS", "ADC", "ADC", "ADC", "STZ", "ADC", "ROR", "ADC", "SEI", "ADC", "PLY", "TDC", "JMP", "ADC", "ROR", "ADC",
        "BRA", "STA", "BRL", "STA", "STY", "STA", "STX", "STA", "DEY", "BIT", "TXA", "PHB", "STY", "STA", "STX", "STA",
        "BCC", "STA", "STA", "STA", "STY", "STA", "STX", "STA", "TYA", "STA", "TXS", "TXY", "STZ", "STA", "STZ", "STA",
        "LDY", "LDA", "LDX", "LDA", "LDY", "LDA", "LDX", "LDA", "TAY", "LDA", "TAX", "PLB", "LDY", "LDA", "LDX", "LDA",
        "BCS", "LDA", "LDA", "LDA", "LDY", "LDA", "LDX", "LDA", "CLV", "LDA", "TSX", "TYX", "LDY", "LDA", "LDX", "LDA",
        "CPY", "CMP", "REP", "CMP", "CPY", "CMP", "DEC", "CMP", "INY", "CMP", "DEX", "WAI", "CPY", "CMP", "DEC", "CMP",
        "BNE", "CMP", "CMP", "CMP", "PEI", "CMP", "DEC", "CMP", "CLD", "CMP", "PHX", "STP", "JML", "CMP", "DEC", "CMP",
        "CPX", "SBC", "SEP", "SBC", "CPX", "SBC", "INC", "SBC", "INX", "SBC", "NOP", "XBA", "CPX", "SBC", "INC", "SBC",
        "BEQ", "SBC", "SBC", "SBC", "PEA", "SBC", "INC", "SBC", "SED", "SBC", "PLX", "XCE", "JSR", "SBC", "INC", "SBC"
    };

    public static readonly AddressMode[] AddressingModes =
    {
        AddressMode.Constant8, AddressMode.DirectPageXIndexIndirect, AddressMode.Constant8, AddressMode.DirectPageSIndex,
        AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Accumulator, AddressMode.Implied,
        AddressMode.Address, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.DirectPage, AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Accumulator, AddressMode.Implied,
        AddressMode.Address, AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.LongXIndex,

        AddressMode.Address, AddressMode.DirectPageXIndexIndirect, AddressMode.Long, AddressMode.DirectPageSIndex,
        AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Accumulator, AddressMode.Implied,
        AddressMode.Address, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Accumulator, AddressMode.Implied,
        AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.LongXIndex,

        AddressMode.Implied, AddressMode.DirectPageXIndexIndirect, AddressMode.Constant8, AddressMode.DirectPageSIndex,
        AddressMode.BlockMove, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Accumulator, AddressMode.Implied,
        AddressMode.Address, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.BlockMove, AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Implied, AddressMode.Implied,
        AddressMode.Long, AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.LongXIndex,

        AddressMode.Implied, AddressMode.DirectPageXIndexIndirect, AddressMode.Relative16, AddressMode.DirectPageSIndex,
        AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Accumulator, AddressMode.Implied,
        AddressMode.AddressIndirect, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Implied, AddressMode.Implied,
        AddressMode.AddressXIndexIndirect, AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.LongXIndex,

        AddressMode.Relative8, AddressMode.DirectPageXIndexIndirect, AddressMode.Relative16, AddressMode.DirectPageSIndex,
        AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Implied, AddressMode.Implied,
        AddressMode.Address, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageYIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Implied, AddressMode.Implied,
        AddressMode.Address, AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.LongXIndex,

        AddressMode.ImmediateXFlagDependent, AddressMode.DirectPageXIndexIndirect, AddressMode.ImmediateXFlagDependent, AddressMode.DirectPageSIndex,
        AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Implied, AddressMode.Implied,
        AddressMode.Address, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageYIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Implied, AddressMode.Implied,
        AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.AddressYIndex, AddressMode.LongXIndex,

        AddressMode.ImmediateXFlagDependent, AddressMode.DirectPageXIndexIndirect, AddressMode.Constant8, AddressMode.DirectPageSIndex,
        AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Implied, AddressMode.Implied,
        AddressMode.Address, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.DirectPageIndirect, AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Implied, AddressMode.Implied,
        AddressMode.AddressLongIndirect, AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.LongXIndex,

        AddressMode.ImmediateXFlagDependent, AddressMode.DirectPageXIndexIndirect, AddressMode.Constant8, AddressMode.DirectPageSIndex,
        AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPage, AddressMode.DirectPageLongIndirect,
        AddressMode.Implied, AddressMode.ImmediateMFlagDependent, AddressMode.Implied, AddressMode.Implied,
        AddressMode.Address, AddressMode.Address, AddressMode.Address, AddressMode.Long,
        AddressMode.Relative8, AddressMode.DirectPageIndirectYIndex, AddressMode.DirectPageIndirect, AddressMode.DirectPageSIndexIndirectYIndex,
        AddressMode.Address, AddressMode.DirectPageXIndex, AddressMode.DirectPageXIndex, AddressMode.DirectPageLongIndirectYIndex,
        AddressMode.Implied, AddressMode.AddressYIndex, AddressMode.Implied, AddressMode.Implied,
        AddressMode.AddressXIndexIndirect, AddressMode.AddressXIndex, AddressMode.AddressXIndex, AddressMode.LongXIndex,
    };
}