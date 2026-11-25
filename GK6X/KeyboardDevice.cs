using HidSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace GK6X
{
    public class KeyboardDevice : IDisposable
    {
        internal HidDevice device;
        internal HidStream stream;
        public KeyboardState State { get; internal set; }
        public bool IsClosed { get; private set; }
        private int lastSentMessage;
        private object locker = new object();

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            IsClosed = true;
            try
            {
                if (stream != null)
                {
                    stream.Close();
                }
                stream = null;
                device = null;
            }
            catch
            {
            }
        }

        internal void StartPingThread()
        {
            stream.InterruptRequested += Stream_InterruptRequested;
            ThreadPool.QueueUserWorkItem((object obj) =>
            {
                while (!IsClosed)
                {
                    int tick = Environment.TickCount;
                    // The keyboard needs real ping packets (not just any traffic): in the "driver" layer it stops
                    // answering realtime lighting after ~5 seconds without one.
                    if (tick < lastSentMessage || tick - 300 > lastSentMessage || tick < lastPingTick || tick - 500 > lastPingTick)
                    {
                        SendPing();
                        if (tick - 2000 > lastSentMessage)
                        {
                            Close();
                            break;
                        }
                    }
                    Thread.Sleep(100);
                }
            });
        }

        private void Stream_InterruptRequested(object sender, EventArgs e)
        {
        }

        private int lastPingTick;

        private void SendPing()
        {
            lastPingTick = Environment.TickCount;
            WritePacketNoResponse(OpCodes.Ping, 0, null);
        }

        public void SetLayer(KeyboardLayer layer)
        {
            WritePacketNoResponse(OpCodes.SetLayer, (byte)layer, null);
        }

        public void SetLighting(KeyboardLayer layer, UserDataFile userData)
        {
            List<UserDataFile.LightingEffect> lightingEffects = userData.GetLightingEffects(layer);
            if (lightingEffects.Count > 0 || userData.NoLighting || userData.NoLightingLayers.Contains(layer))
            {
                WritePacketNoResponse(OpCodes.LayerResetDataType, (byte)layer, null, (byte)KeyboardLayerDataType.Lighting);
            }
            if (lightingEffects.Count > 0)
            {
                using (Packet packet = new Packet())
                {
                    const int maxEffects = UserDataFile.LightingEffect.MaxEffects;
                    packet.WriteBytes(new byte[maxEffects * (4 * 4)]);
                    List<Tuple<int, int, int, int>> dataOffsets = new List<Tuple<int, int, int, int>>();

                    for (int i = 0; i < Math.Min(maxEffects, lightingEffects.Count); i++)
                    {
                        UserDataFile.LightingEffect lightingEffect = lightingEffects[i];
                        if (lightingEffect != null)
                        {
                            int data1Offset = packet.Index;
                            int data1Count = 0;
                            switch (lightingEffect.Type)
                            {
                                case LightingEffectType.Static:
                                    {
                                        data1Count = 1;
                                        packet.WriteUInt16((ushort)lightingEffect.Type);
                                        packet.WriteUInt16((ushort)(UserDataFile.LightingEffect.NumStaticLightingBytes));
                                        uint[] keyColors = new uint[UserDataFile.LightingEffect.NumStaticLightingBytes / 4];
                                        foreach (KeyValuePair<int, uint> keyColor in lightingEffect.KeyColors)
                                        {
                                            keyColors[keyColor.Key] = keyColor.Value;
                                        }
                                        foreach (uint color in keyColors)
                                        {
                                            packet.WriteUInt32(color);
                                        }
                                    }
                                    break;
                                case LightingEffectType.Dynamic:
                                    {
                                        data1Count = lightingEffect.TotalFrames;
                                        foreach (UserDataFile.LightingEffect.Frame frame in lightingEffect.Frames)
                                        {
                                            for (int j = 0; j < frame.Count; j++)
                                            {
                                                packet.WriteUInt16((ushort)lightingEffect.Type);
                                                packet.WriteUInt16(22);// 22 bytes of data (176 bits)
                                                byte[] byteBits = new byte[22];
                                                foreach (int keyCode in frame.KeyCodes)
                                                {
                                                    int byteIndex = keyCode / 8;
                                                    int bitIndex = keyCode % 8;
                                                    byteBits[byteIndex] |= (byte)(1 << bitIndex);
                                                }
                                                packet.WriteBytes(byteBits);
                                            }
                                        }
                                    }
                                    break;
                            }

                            int data2Offset = packet.Index;
                            int data2Count = 0;
                            switch (lightingEffect.Type)
                            {
                                case LightingEffectType.Static:
                                    data2Offset = 0;
                                    data2Count = 0;
                                    break;
                                case LightingEffectType.Dynamic:
                                    data2Count = lightingEffect.Params.Count;
                                    foreach (UserDataFile.LightingEffect.Param param in lightingEffect.Params)
                                    {
                                        packet.WriteByte((byte)param.ColorType);
                                        packet.WriteByte(32);// The size of the param buffer? (total size including self)

                                        // The keys impacted by the param (as a bit array)
                                        byte[] byteBits = new byte[22];
                                        foreach (int keyCode in param.Keys)
                                        {
                                            int byteIndex = keyCode / 8;
                                            int bitIndex = keyCode % 8;
                                            byteBits[byteIndex] |= (byte)(1 << bitIndex);
                                        }
                                        packet.WriteBytes(byteBits);

                                        packet.WriteUInt32(param.Color);
                                        if (param.UseRawValues)
                                        {
                                            packet.WriteUInt16((ushort)param.Val1);
                                            packet.WriteUInt16((ushort)param.Val2);
                                        }
                                        else
                                        {
                                            switch (param.ColorType)
                                            {
                                                case LightingEffectColorType.Breathing:
                                                    packet.WriteUInt16((ushort)(param.Val1 == 0 ? 0 : 100 / param.Val1));
                                                    packet.WriteUInt16((ushort)param.Val2);// "StayCount"
                                                    break;
                                                default:
                                                    // NOTE: RGB values seem to use 0x26 for the 2nd value? Is this just a random uninitialized variable?
                                                    packet.WriteUInt16((ushort)(param.Val1 == 0 ? 0 : 360 / param.Val1));
                                                    packet.WriteUInt16(0);
                                                    break;
                                            }
                                        }
                                    }
                                    break;
                            }

                            dataOffsets.Add(new Tuple<int, int, int, int>(data1Offset, data1Count, data2Offset, data2Count));
                        }
                    }

                    int tempIndex = packet.Index;
                    packet.Index = 0;
                    for (int i = 0; i < maxEffects; i++)
                    {
                        Tuple<int, int, int, int> offsets = i < lightingEffects.Count ? dataOffsets[i] : null;
                        if (offsets != null)
                        {
                            packet.WriteInt32(offsets.Item1);
                            packet.WriteInt32(offsets.Item2);
                            packet.WriteInt32(offsets.Item3);
                            packet.WriteInt32(offsets.Item4);
                        }
                        else
                        {
                            packet.WriteInt32(-1);
                            packet.WriteInt32(-1);
                            packet.WriteInt32(-1);
                            packet.WriteInt32(-1);
                        }
                    }
                    packet.Index = tempIndex;
                    WritePacketNoResponse(OpCodes.LayerSetLightValues, (byte)layer, packet);
                }
            }
        }

        public void SetMacros(KeyboardLayer layer, UserDataFile userData)
        {
            WritePacketNoResponse(OpCodes.LayerResetDataType, (byte)layer, null, (byte)KeyboardLayerDataType.Macros);
            // The following check has been disabled so that macros can be assigned without having to setup the keys
            /*if (userData.GetNumMacros(layer) == 0)
            {
                return;
            }*/
            using (Packet packet = new Packet())
            {
                // TODO: This should be improved to only send the macros to the layers that require the macro
                foreach (UserDataFile.Macro macro in userData.Macros.Values)
                {
                    if (macro.Id < 0)
                    {
                        continue;
                    }
                    if (macro.Actions.Count * 2 > byte.MaxValue)
                    {
                        Program.Log("Macro '" + macro.Name + "' has too many actions (" + macro.Actions.Count +
                            ", limit is " + (byte.MaxValue / 2) + ")");
                        continue;
                    }
                    if (macro.Actions.Count == 0 && macro.Id >= 0)
                    {
                        Program.Log("Macro '" + macro.Name + "' doesn't have any actions!");
                        continue;
                    }

                    packet.WriteUInt16(0x55AA);// Macro magic (21930 / 0x55AA / AA 55)

                    // Crc to be filled out once all data is written
                    int crcIndex = packet.Index;
                    packet.WriteUInt16(0);

                    byte numActionInts = (byte)((macro.Actions.Count * 2) - (macro.UseTrailingDelay ? 0 : 1));
                    packet.WriteByte(numActionInts);
                    packet.WriteByte((byte)macro.Id);
                    packet.WriteByte((byte)macro.RepeatType);
                    packet.WriteByte(macro.RepeatCount);

                    int crcDataStartIndex = packet.Index;
                    int crcDataEndIndex = packet.Index + (numActionInts * 4);

                    for (int i = 0; i < 63; i++)
                    {
                        if (i < macro.Actions.Count)
                        {
                            UserDataFile.Macro.Action action = macro.Actions[i];
                            packet.WriteByte(action.KeyCode);
                            packet.WriteByte((byte)action.Modifier);
                            packet.WriteByte((byte)action.State);
                            packet.WriteByte((byte)action.Type);

                            if (i < macro.Actions.Count - 1 || (macro.UseTrailingDelay && i == macro.Actions.Count - 1))
                            {
                                packet.WriteUInt16(action.Delay);//TODO: Investigate! (ushort)(action.Delay == 0 ? 0 : action.Delay / 2));// Delay seems to be doubled?
                                packet.WriteByte(0);// Always 0?
                                packet.WriteByte(3);// Always 3? (1/2 use no delay, 0/4 (and maybe others) seem to never stop pressing the key)
                            }
                            else
                            {
                                packet.WriteInt32(0);
                            }
                        }
                        else
                        {
                            packet.WriteInt64(0);
                        }
                    }

                    byte[] buffer = packet.GetBuffer();
                    byte[] bytesToCrc = new byte[crcDataEndIndex - crcDataStartIndex];
                    Buffer.BlockCopy(buffer, crcDataStartIndex, bytesToCrc, 0, bytesToCrc.Length);
                    ushort crc = Crc16.GetCrc(bytesToCrc);

                    // Always 0?
                    packet.WriteByte(0);

                    int tempIndex = packet.Index;
                    packet.Index = crcIndex;
                    packet.WriteUInt16(crc);
                    packet.Index = tempIndex;
                }
                WritePacketNoResponse(OpCodes.LayerSetMacros, (byte)layer, packet);
            }
        }

        public void SetKeys(KeyboardLayer layer, uint[] values, bool fn)
        {
            WritePacketNoResponse(OpCodes.LayerResetDataType, (byte)layer, null,
                (byte)(fn ? KeyboardLayerDataType.FnKeySet : KeyboardLayerDataType.KeySet));
            using (Packet packet = new Packet())
            {
                foreach (uint driverValue in values)
                {
                    packet.WriteUInt32(driverValue);
                }
                WritePacketNoResponse(fn ? OpCodes.LayerFnSetKeyValues : OpCodes.LayerSetKeyValues, (byte)layer, packet);
            }
        }

        /// <summary>
        /// Number of 4 byte (R,G,B,A) entries in a realtime lighting frame. Indexed by key LocationCode.
        /// </summary>
        public const int RealtimeLedCount = 132;

        /// <summary>
        /// Sets the "driver" layer key table (op 0x16/0x01). Index = LogicCode, value = DriverValue.
        /// Without this table (or the vendor software) the driver layer has no key assignments.
        /// </summary>
        public bool SetDriverKeyTable(uint[] values)
        {
            using (Packet packet = new Packet())
            {
                foreach (uint driverValue in values)
                {
                    packet.WriteUInt32(driverValue);
                }
                using (Packet result = WritePacket(OpCodes.DriverLayerSetKeyValues,
                    (byte)OpCodes_SetDriverLayerKeyValues.KeySet, packet, 0, true))
                {
                    return result != null;
                }
            }
        }

        /// <summary>
        /// Sets the built-in lighting config used by the "driver" layer (op 0x17/0x01, 9 byte body).
        /// </summary>
        public bool SetDriverLayerConfig(byte model, byte subModel, byte light, byte speed, byte dir,
            byte r, byte g, byte b, bool enable)
        {
            using (Packet packet = new Packet())
            {
                packet.WriteByte(model);
                packet.WriteByte(subModel);
                packet.WriteByte(light);
                packet.WriteByte(speed);
                packet.WriteByte(dir);
                packet.WriteByte(r);
                packet.WriteByte(g);
                packet.WriteByte(b);
                packet.WriteByte((byte)(enable ? 1 : 0));
                using (Packet result = WritePacket(OpCodes.DriverLayerSetConfig, 1, packet))
                {
                    return result != null;
                }
            }
        }

        /// <summary>
        /// Sends one complete realtime lighting frame (only meaningful in the "driver" layer).
        /// <paramref name="rgba"/> must contain <see cref="RealtimeLedCount"/> * 4 bytes (R,G,B,A per LocationCode).
        /// </summary>
        public bool SendRealtimeFrame(byte[] rgba)
        {
            if (rgba == null || rgba.Length != RealtimeLedCount * 4)
            {
                throw new ArgumentException("Bad realtime frame length");
            }
            // The official software pads the frame to a multiple of 0x38 (560 bytes)
            byte[] padded = new byte[560];
            Buffer.BlockCopy(rgba, 0, padded, 0, rgba.Length);
            using (Packet packet = new Packet())
            {
                packet.WriteBytes(padded);
                using (Packet result = WritePacket(OpCodes.DriverLayerUpdateRealtimeLighting,
                    (byte)OpCodes_DriverLayerUpdateRealtimeLighting.Update, packet, 0, true))
                {
                    if (result == null)
                    {
                        return false;
                    }
                }
            }
            using (Packet result = WritePacket(OpCodes.DriverLayerUpdateRealtimeLighting,
                (byte)OpCodes_DriverLayerUpdateRealtimeLighting.UpdateComplete, null))
            {
                return result != null;
            }
        }

        public void SetIdentifyDriverMacros()
        {
            using (Packet packet = new Packet())
            {
                for (byte i = 0; i < byte.MaxValue; i++)
                {
                    packet.WriteUInt32((uint)(0x0A010000 | i));
                }
                WritePacketNoResponse(OpCodes.DriverLayerSetKeyValues, (byte)OpCodes_SetDriverLayerKeyValues.KeySet, packet);
            }
            using (Packet packet = new Packet())
            {
                packet.WriteByte(1);
                WritePacketNoResponse(OpCodes.DriverMacro, (byte)OpCodes_DriverMacro.BeginEnd, packet);
            }
            using (Packet packet = new Packet())
            {
                for (byte i = 0; i < byte.MaxValue; i++)
                {
                    packet.WriteByte(0);
                }
                packet.WriteByte(0);
                WritePacketNoResponse(OpCodes.DriverMacro, (byte)OpCodes_DriverMacro.KeyboardState, packet);
            }
            using (Packet packet = new Packet())
            {
                packet.WriteByte(0);
                WritePacketNoResponse(OpCodes.DriverMacro, (byte)OpCodes_DriverMacro.BeginEnd, packet);
            }
        }

        /// <summary>Number of packets the keyboard did not acknowledge (used to report "apply failed").</summary>
        public int FailedPackets;

        public void WritePacketNoResponse(OpCodes op1, byte op2, Packet packet, byte op3 = 0)
        {
            using (Packet response = new Packet())
            using (Packet result = WritePacket(op1, op2, packet, op3))
            {
                if (result == null)
                {
                    FailedPackets++;
                }
            }
        }

        /// <summary>
        /// Builds one 65 byte output report (report id 0 + 64 bytes) of a possibly multi-report transfer.
        /// Layout of the 64 bytes: [0] op1, [1] op2, [2..5] offset / length fields, [6..7] crc16, [8..63] data (max 0x38).
        /// </summary>
        internal static byte[] BuildReport(OpCodes op1, byte op2, byte op3, byte[] completeBuffer, int numPackets, int offset)
        {
            bool allowsLongOffset = false;
            int offsetOffset = 2;
            int lengthOffset = 4;
            switch (op1)
            {
                case OpCodes.DriverLayerSetKeyValues:
                case OpCodes.DriverLayerUpdateRealtimeLighting:
                case OpCodes.LayerSetLightValues:
                    offsetOffset = 2;
                    lengthOffset = 5;
                    allowsLongOffset = true;
                    break;
                case OpCodes.LayerSetKeyPressLightingEffect:
                case OpCodes.LayerSetKeyValues:
                case OpCodes.LayerFnSetKeyValues:
                case OpCodes.LayerSetMacros:
                    offsetOffset = 2;
                    lengthOffset = 4;
                    break;
            }

            const int reportHeaderLen = 1;
            byte[] report = new byte[65];
            report[1] = (byte)op1;
            report[2] = op2;
            if (op3 > 0)
            {
                // Not really an op, used for setting keyboard data buffers
                report[3] = op3;
            }
            if (completeBuffer != null)
            {
                int numBytesToWrite = Math.Max(0, Math.Min(0x38, completeBuffer.Length - offset));
                Buffer.BlockCopy(completeBuffer, offset, report, reportHeaderLen + 8, numBytesToWrite);
                if (numPackets > 1)
                {
                    report[reportHeaderLen + offsetOffset + 0] = (byte)offset;
                    report[reportHeaderLen + offsetOffset + 1] = (byte)(offset >> 8);
                    if (allowsLongOffset)
                    {
                        report[reportHeaderLen + offsetOffset + 2] = (byte)(offset >> 16);
                    }
                    report[reportHeaderLen + lengthOffset] = (byte)numBytesToWrite;
                }
            }
            Crc16.InsertCrc(report, reportHeaderLen, 6 + reportHeaderLen);
            return report;
        }

        public Packet WritePacket(OpCodes op1, byte op2, Packet packet, byte op3 = 0, bool exactChunks = false)
        {
            lock (locker)
            {
                int numPackets = 1;
                int offset = 0;
                byte[] completeBuffer = null;
                if (packet != null)
                {
                    completeBuffer = packet.GetWrittenBuffer();
                    // exactChunks: don't send a trailing empty chunk when the data is an exact multiple of 0x38
                    // (used by realtime lighting, 560 bytes == 10 * 0x38)
                    numPackets = exactChunks ? Math.Max(1, (completeBuffer.Length + 0x37) / 0x38) : (completeBuffer.Length / 0x38) + 1;
                }

                try
                {
                    for (int i = 0; i < numPackets; i++)
                    {
                        byte[] report = BuildReport(op1, op2, op3, completeBuffer, numPackets, offset);
                        stream.Write(report);

                        byte[] resultBuffer = GetResponse((byte)op1, op2);
                        if (resultBuffer[0] != (byte)op1/* || resultBuffer[1] != op2*/)
                        {
                            return null;
                        }
                        if (!Crc16.ValidateCrc(resultBuffer))
                        {
                            return null;
                        }
                        if (i == numPackets - 1)
                        {
                            Packet result = new Packet(true, resultBuffer);
                            result.Index = 8;
                            lastSentMessage = Environment.TickCount;
                            return result;
                        }
                        offset += 0x38;
                    }
                }
                catch
                {
                    return null;
                }
                return null;
            }
        }

        private byte[] GetResponse(byte op1, byte op2)
        {
            // This is a bit of a hack to consume any packets sent from the keyboard which we didn't request
            // such as changing the keyboard layer manually. TODO: Add a proper packet handler and remove this hack.
            const int reportHeaderLen = 1;
            while (true)
            {
                byte[] resultBufferWithReportId = new byte[65];
                try
                {
                    stream.Read(resultBufferWithReportId);
                }
                catch
                {
                    return null;
                }
                if (resultBufferWithReportId[0] != 0)
                {
                    return null;
                }
                if (resultBufferWithReportId[1] == op1)
                {
                    byte[] resultBuffer = new byte[64];
                    Buffer.BlockCopy(resultBufferWithReportId, 1, resultBuffer, 0, resultBuffer.Length);
                    return resultBuffer;
                }
                else if (resultBufferWithReportId[1] == (byte)OpCodes.DriverKeyCallback)
                {
                    byte callbackId = resultBufferWithReportId[3];
                    bool callbackKeyDown = resultBufferWithReportId[4] != 0;
                    if (callbackKeyDown)
                    {
                        KeyboardState.Key key;
                        State.KeysByLogicCode.TryGetValue(callbackId, out key);
                        Program.Log("Index:" + callbackId + " " +
                            (key == null ? "(null)" :
                                "Name:" + key.KeyName + " " +// Name of the key
                                "Indx:" + key.LogicCode + " " +// 'Indx' for logic code / key index
                                "Loc:" + key.LocationCode + " " +// 'Loc' for location code
                                "D:0x" + key.DriverValue.ToString("X8") + " " +// 'D' for DriverValue
                                //"E:" + (DriverValue)key.DriverValue + " " +// 'E' for the Enum variant of the DriverValue
                                "S:" + key.DriverValueName));// <--- 'S' for string representation (use this for mapping keys in your UserData file)
                    }
                }
                else if (!Crc16.ValidateCrc(resultBufferWithReportId, reportHeaderLen, reportHeaderLen + 6))
                {
                    return null;
                }
            }
        }
    }
}
