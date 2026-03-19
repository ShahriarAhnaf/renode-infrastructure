//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class ESP32S3_InterruptMatrix : IDoubleWordPeripheral, IKnownSize
    {
        public ESP32S3_InterruptMatrix(IMachine machine)
        {
            this.machine = machine;
            routingTable = new uint[SourceCount];
            var registersMap = new Dictionary<long, DoubleWordRegister>();

            for(int i = 0; i < SourceCount; i++)
            {
                var sourceIndex = i;
                registersMap.Add(i * 4, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: $"MAP_{sourceIndex}",
                        writeCallback: (_, value) =>
                        {
                            routingTable[sourceIndex] = (uint)value;
                        },
                        valueProviderCallback: _ => routingTable[sourceIndex])
                    .WithReservedBits(5, 27)
                );
            }

            // Clock gate register at the end
            registersMap.Add(0x800, new DoubleWordRegister(this, 0x00000001)
                .WithTaggedFlag("INTERRUPT_CORE0_CLOCK_GATE", 0)
                .WithReservedBits(1, 31)
            );

            // Date register
            registersMap.Add(0x7FC, new DoubleWordRegister(this, 0x02101180)
                .WithValueField(0, 28, name: "INTERRUPT_CORE0_DATE")
                .WithReservedBits(28, 4)
            );

            registers = new DoubleWordRegisterCollection(this, registersMap);
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public void Reset()
        {
            registers.Reset();
            for(int i = 0; i < SourceCount; i++)
            {
                routingTable[i] = 0;
            }
        }

        public long Size => 0x900;

        private const int SourceCount = 99;

        private readonly uint[] routingTable;
        private readonly DoubleWordRegisterCollection registers;
        private readonly IMachine machine;
    }
}
