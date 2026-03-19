// ESP32S3_I2C.cs -- ESP32-S3 I2C master controller for Renode
//
// Ported from capstone ESP32_I2C.cs with ESP32-S3 adaptations:
//   - Base addresses: 0x60013000 (I2C0), 0x60027000 (I2C1)
//     vs ESP32: 0x3FF53000, 0x3FF67000
//   - Register offsets are compatible between ESP32 and ESP32-S3
//   - Command FIFO at 0x58+ (same opcode encoding: bits [13:11])
//
// ESP32-S3 I2C register map (offsets from base):
//   0x00  I2C_SCL_LOW_PERIOD_REG
//   0x04  I2C_CTR_REG           (TRANS_START = bit 5)
//   0x08  I2C_SR_REG            (status)
//   0x0C  I2C_TO_REG            (timeout)
//   0x10  I2C_SLAVE_ADDR_REG
//   0x14  I2C_RXFIFO_ST_REG
//   0x18  I2C_FIFO_CONF_REG
//   0x1C  I2C_DATA_REG          (FIFO data port)
//   0x20  I2C_INT_RAW_REG
//   0x24  I2C_INT_CLR_REG
//   0x28  I2C_INT_ENA_REG
//   0x2C  I2C_INT_STATUS_REG
//   0x58+ I2C_COMD0..COMD15_REG (command registers)

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.I2C;

namespace Antmicro.Renode.Peripherals.I2C
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32S3_I2C : SimpleContainer<II2CPeripheral>, IDoubleWordPeripheral, IKnownSize
    {
        public ESP32S3_I2C(IMachine machine) : base(machine)
        {
            txFifo = new Queue<byte>();
            rxFifo = new Queue<byte>();
            commands = new uint[16];
            IRQ = new GPIO();
            Reset();
        }

        public GPIO IRQ { get; }

        public long Size => 0x100;

        public override void Reset()
        {
            txFifo.Clear();
            rxFifo.Clear();
            Array.Clear(commands, 0, commands.Length);
            statusReg = 0;
            intRawReg = 0;
            intEnaReg = 0;
            controlReg = 0;
            slaveAddr = 0;
            fifoConfReg = 0;
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case REG_CTR:
                    return controlReg;

                case REG_SR:
                    return BuildStatusReg();

                case REG_FIFO_CONF:
                    return fifoConfReg;

                case REG_DATA:
                    if(rxFifo.Count > 0)
                    {
                        return rxFifo.Dequeue();
                    }
                    return 0;

                case REG_INT_RAW:
                    return intRawReg;

                case REG_INT_STATUS:
                    return intRawReg & intEnaReg;

                case REG_INT_ENA:
                    return intEnaReg;

                case REG_RXFIFO_ST:
                    return (uint)rxFifo.Count;

                default:
                    if(offset >= REG_COMD_BASE && offset < REG_COMD_BASE + 16 * 4)
                    {
                        int idx = (int)(offset - REG_COMD_BASE) / 4;
                        return commands[idx];
                    }
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case REG_CTR:
                    controlReg = value;
                    if((value & CTR_TRANS_START) != 0)
                    {
                        ExecuteTransaction();
                        controlReg &= ~CTR_TRANS_START;
                    }
                    break;

                case REG_SLAVE_ADDR:
                    slaveAddr = value;
                    break;

                case REG_FIFO_CONF:
                    fifoConfReg = value;
                    if((value & FIFO_CONF_TX_RST) != 0)
                    {
                        txFifo.Clear();
                    }
                    if((value & FIFO_CONF_RX_RST) != 0)
                    {
                        rxFifo.Clear();
                    }
                    break;

                case REG_DATA:
                    txFifo.Enqueue((byte)(value & 0xFF));
                    break;

                case REG_INT_CLR:
                    intRawReg &= ~value;
                    break;

                case REG_INT_ENA:
                    intEnaReg = value;
                    break;

                default:
                    if(offset >= REG_COMD_BASE && offset < REG_COMD_BASE + 16 * 4)
                    {
                        int idx = (int)(offset - REG_COMD_BASE) / 4;
                        commands[idx] = value;
                    }
                    break;
            }
        }

        private void ExecuteTransaction()
        {
            byte address = (byte)((slaveAddr >> 1) & 0x7F);
            bool isRead = (slaveAddr & 1) != 0;

            II2CPeripheral slave = null;
            if(!TryGetByAddress((int)address, out slave))
            {
                this.Log(LogLevel.Warning, "No I2C slave at address 0x{0:X2}", address);
                intRawReg |= INT_NACK;
                return;
            }

            for(int i = 0; i < commands.Length; i++)
            {
                uint cmd = commands[i];
                if(cmd == 0)
                {
                    break;
                }

                commands[i] |= COMD_DONE;

                byte opcode = (byte)((cmd >> 11) & 0x7);
                byte byteCount = (byte)(cmd & 0xFF);

                switch(opcode)
                {
                    case OP_RSTART:
                        break;

                    case OP_WRITE:
                        {
                            var data = new byte[byteCount];
                            for(int b = 0; b < byteCount && txFifo.Count > 0; b++)
                            {
                                data[b] = txFifo.Dequeue();
                            }
                            slave.Write(data);
                        }
                        break;

                    case OP_READ:
                        {
                            var data = slave.Read(byteCount);
                            foreach(var b in data)
                            {
                                rxFifo.Enqueue(b);
                            }
                        }
                        break;

                    case OP_STOP:
                        slave.FinishTransmission();
                        break;

                    case OP_END:
                        goto done;
                }
            }

            done:
            intRawReg |= INT_TRANS_COMPLETE;
        }

        private uint BuildStatusReg()
        {
            uint sr = 0;
            sr |= (uint)(txFifo.Count & 0x1F) << 18;
            sr |= (uint)(rxFifo.Count & 0x1F) << 8;
            return sr;
        }

        private readonly Queue<byte> txFifo;
        private readonly Queue<byte> rxFifo;
        private readonly uint[] commands;

        private uint statusReg;
        private uint intRawReg;
        private uint intEnaReg;
        private uint controlReg;
        private uint slaveAddr;
        private uint fifoConfReg;

        private const long REG_SCL_LOW = 0x00;
        private const long REG_CTR = 0x04;
        private const long REG_SR = 0x08;
        private const long REG_TO = 0x0C;
        private const long REG_SLAVE_ADDR = 0x10;
        private const long REG_RXFIFO_ST = 0x14;
        private const long REG_FIFO_CONF = 0x18;
        private const long REG_DATA = 0x1C;
        private const long REG_INT_RAW = 0x20;
        private const long REG_INT_CLR = 0x24;
        private const long REG_INT_ENA = 0x28;
        private const long REG_INT_STATUS = 0x2C;
        private const long REG_COMD_BASE = 0x58;

        private const uint CTR_TRANS_START = (1 << 5);

        private const uint FIFO_CONF_RX_RST = (1 << 12);
        private const uint FIFO_CONF_TX_RST = (1 << 13);

        private const byte OP_RSTART = 0;
        private const byte OP_WRITE = 1;
        private const byte OP_READ = 2;
        private const byte OP_STOP = 3;
        private const byte OP_END = 4;

        private const uint COMD_DONE = (1u << 31);

        private const uint INT_TRANS_COMPLETE = (1 << 7);
        private const uint INT_NACK = (1 << 10);
    }
}
