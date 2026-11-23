using System;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

/// <summary>One GIP message as it travels on the USB interrupt pipe.</summary>
public readonly record struct GipMessage(byte Command, byte Options, byte Sequence, byte[] Payload)
{
    public bool Internal => (Options & GipCodec.OptInternal) != 0;
    public bool WantsAck => (Options & GipCodec.OptAckRequested) != 0;
    public bool Chunked => (Options & GipCodec.OptChunk) != 0;
}

/// <summary>
/// Pure GIP framing and the messages direct mode (docs/WINUSB_DIRECT_MODE.md) needs, after the Linux xpad / xone
/// drivers and the reports Windows wrote to RW3's virtual pad: <c>[command][options][sequence][length varint][payload]</c>.
/// Options: 0x20 internal (system) message, 0x10 the sender wants an ACK, 0x80 chunked. Contract-tested.
/// </summary>
public static class GipCodec
{
    public const byte CmdAck = 0x01, CmdAnnounce = 0x02, CmdStatus = 0x03, CmdIdentify = 0x04, CmdPower = 0x05,
        CmdAuth = 0x06, CmdGuide = 0x07, CmdRumble = 0x09, CmdLed = 0x0A, CmdRazer = 0x0F, CmdInput = 0x20;
    public const byte OptAckRequested = 0x10, OptInternal = 0x20, OptChunk = 0x80;

    /// <summary>GIP length: 7 bits per byte, low bits first, 0x80 = more bytes follow.</summary>
    public static int WriteVarint(Span<byte> dst, int value)
    {
        int n = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            dst[n++] = b;
        } while (value != 0);
        return n;
    }

    public static byte[] Encode(byte command, byte options, byte sequence, ReadOnlySpan<byte> payload)
    {
        Span<byte> len = stackalloc byte[4];
        int ln = WriteVarint(len, payload.Length);
        var b = new byte[3 + ln + payload.Length];
        b[0] = command; b[1] = options; b[2] = sequence;
        len[..ln].CopyTo(b.AsSpan(3));
        payload.CopyTo(b.AsSpan(3 + ln));
        return b;
    }

    /// <summary>Splits one USB transfer (it may hold several messages) into messages; stops at a truncated one.</summary>
    public static List<GipMessage> Decode(ReadOnlySpan<byte> transfer)
    {
        var list = new List<GipMessage>();
        int i = 0;
        while (i + 4 <= transfer.Length)
        {
            byte cmd = transfer[i], opt = transfer[i + 1], seq = transfer[i + 2];
            int len = 0, shift = 0, j = i + 3;
            while (j < transfer.Length)
            {
                byte b = transfer[j++];
                len |= (b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0 || shift > 21) break;
            }
            // a chunked message carries an offset varint after the length; the length counts only the data after it
            // (xone gip_parse_header). The payload keeps the offset varint in front of the data.
            int dataStart = j;
            if ((opt & OptChunk) != 0)
                while (dataStart < transfer.Length && (transfer[dataStart++] & 0x80) != 0) { }
            if (dataStart + len > transfer.Length) break;
            list.Add(new GipMessage(cmd, opt, seq, transfer.Slice(j, dataStart - j + len).ToArray()));
            i = dataStart + len;
            if (cmd == 0 && opt == 0 && len == 0) break;
        }
        return list;
    }

    /// <summary>Power on (system message 0x05, mode 0x00): the pad starts sending input after it.</summary>
    public static byte[] PowerOn(byte sequence) => Encode(CmdPower, OptInternal, sequence, new byte[] { 0x00 });

    /// <summary>Power reset (system message 0x05, mode 0x07, as in xone): the pad re-announces itself like after a replug.
    /// Needed when RW3 opens a pad that was already connected: power-on alone did not start its input (2026-11-22).</summary>
    public static byte[] PowerReset(byte sequence) => Encode(CmdPower, OptInternal, sequence, new byte[] { 0x07 });

    /// <summary>Xbox button LED on (0x0A, as xpad's GIP_LED_ON: mode 0x01 steady, brightness 0x14). Windows' driver sends it
    /// after power-on; without Windows in direct mode nobody did, and the pad's LEDs stayed dark (2026-11-23).</summary>
    public static byte[] LedOn(byte sequence, byte brightness = 0x14) => Encode(CmdLed, OptInternal, sequence, new byte[] { 0x00, 0x01, brightness });

    /// <summary>ACK for a message that asked for one (xone gip_send_ack): echoes its command, options and length.</summary>
    public static byte[] Ack(in GipMessage m) =>
        Encode(CmdAck, OptInternal, m.Sequence, new byte[]
        {
            0x00, m.Command, (byte)(OptInternal | (m.Options & 0x0F)),
            (byte)(m.Payload.Length & 0xFF), (byte)(m.Payload.Length >> 8), 0x00, 0x00, 0x00, 0x00
        });

    /// <summary>Standard rumble 0x09: all four motors, 0..1 each → 0..100, longest duration, as Windows sends it.</summary>
    public static byte[] Rumble(byte sequence, double bodyLeft, double bodyRight, double triggerLeft, double triggerRight)
    {
        static byte Q(double v) => (byte)Math.Round((double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0) * 100);
        return Encode(CmdRumble, 0x00, sequence, new byte[] { 0x00, 0x0F, Q(triggerLeft), Q(triggerRight), Q(bodyLeft), Q(bodyRight), 0xFF, 0x00, 0xEB });
    }

    /// <summary>Razer's 60-byte 0x0F command (Sensa frames, RGB, battery…; built by SensaXboxFrameEncoder.BuildGipPacket).
    /// The options byte WGI uses for it (GipMessageClass.Command) is still to be confirmed on hardware.</summary>
    public static byte[] Razer(byte sequence, ReadOnlySpan<byte> packet60, byte options = 0x00) => Encode(CmdRazer, options, sequence, packet60);

    /// <summary>Razer command in its 60-byte tunnel form (as Razer's own tools wrap it in 0x0F):
    /// [status 0, transaction, 0, 0, 0, args length, class, command, args…], XOR of bytes 2..57 at byte 58.</summary>
    public static byte[] RazerPacket(byte transaction, byte commandClass, byte command, ReadOnlySpan<byte> args)
    {
        if (args.Length > 48) throw new ArgumentOutOfRangeException(nameof(args));
        var p = new byte[60];
        p[1] = transaction; p[5] = (byte)args.Length; p[6] = commandClass; p[7] = command;
        args.CopyTo(p.AsSpan(8));
        byte x = 0;
        for (int i = 2; i < 58; i++) x ^= p[i];
        p[58] = x;
        return p;
    }

    /// <summary>Razer lighting (class 0x0F, RAM, profile 0, all zones 0x05): brightness 0..255 (command 0x04).</summary>
    public static byte[] RazerBrightness(byte transaction, byte brightness) => RazerPacket(transaction, 0x0F, 0x04, new byte[] { 0x00, 0x05, brightness });

    /// <summary>Razer lighting: one static colour (command 0x02, effect 0x0D, custom colour slot).</summary>
    public static byte[] RazerStatic(byte transaction, byte r, byte g, byte b) =>
        RazerPacket(transaction, 0x0F, 0x02, new byte[] { 0x00, 0x05, 0x0D, 0x08, 0x01, 0x01, r, g, b });

    /// <summary>Input report 0x20 → XInput-style state. Payload offsets (xpad xpadone_process_packet minus its 4-byte
    /// header): [0] Menu 0x04, View 0x08, A 0x10, B 0x20, X 0x40, Y 0x80; [1] up, down, left, right, LB, RB, LS, RS;
    /// [2..3] LT 0..1023, [4..5] RT, [6..13] LX, LY, RX, RY (signed, up = +).</summary>
    public static bool TryParseInput(ReadOnlySpan<byte> payload, bool guide, out XInputPad pad)
    {
        pad = default;
        if (payload.Length < 14) return false;
        byte b0 = payload[0], b1 = payload[1];
        ushort buttons = 0;
        void Map(bool on, ushort bit) { if (on) buttons |= bit; }
        Map((b0 & 0x04) != 0, XInputPad.Start); Map((b0 & 0x08) != 0, XInputPad.Back);
        Map((b0 & 0x10) != 0, XInputPad.A); Map((b0 & 0x20) != 0, XInputPad.B);
        Map((b0 & 0x40) != 0, XInputPad.X); Map((b0 & 0x80) != 0, XInputPad.Y);
        Map((b1 & 0x01) != 0, XInputPad.DpadUp); Map((b1 & 0x02) != 0, XInputPad.DpadDown);
        Map((b1 & 0x04) != 0, XInputPad.DpadLeft); Map((b1 & 0x08) != 0, XInputPad.DpadRight);
        Map((b1 & 0x10) != 0, XInputPad.LeftShoulder); Map((b1 & 0x20) != 0, XInputPad.RightShoulder);
        Map((b1 & 0x40) != 0, XInputPad.LeftThumb); Map((b1 & 0x80) != 0, XInputPad.RightThumb);
        Map(guide, XInputPad.Guide);
        static byte Trig(int v) => (byte)Math.Clamp((int)Math.Round(v * 255.0 / 1023.0), 0, 255);
        int lt = BitConverter.ToUInt16(payload[2..4]), rt = BitConverter.ToUInt16(payload[4..6]);
        pad = new XInputPad(buttons, Trig(lt), Trig(rt),
            BitConverter.ToInt16(payload[6..8]), BitConverter.ToInt16(payload[8..10]),
            BitConverter.ToInt16(payload[10..12]), BitConverter.ToInt16(payload[12..14]));
        return true;
    }

    public const byte OptChunkStart = 0x40;

    /// <summary>Identify request (system message 0x04, no payload): the pad answers with its identify data (firmware,
    /// supported messages, interface GUIDs…), in chunks when longer than one transfer.</summary>
    public static byte[] IdentifyRequest(byte sequence) => Encode(CmdIdentify, OptInternal, sequence, ReadOnlySpan<byte>.Empty);

    /// <summary>Splits a chunked payload into its offset varint and data. For the first chunk (option 0x40) the
    /// "offset" is the total length of the whole message.</summary>
    public static (int Offset, int DataStart) ChunkHeader(ReadOnlySpan<byte> payload)
    {
        int value = 0, shift = 0, k = 0;
        while (k < payload.Length)
        {
            byte b = payload[k++];
            value |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0 || shift > 21) break;
        }
        return (value, k);
    }

    /// <summary>Device side of a chunked message (identify reply): the first chunk (options chunk + chunk start) carries
    /// the total length in its offset field, the next ones their offset, and a last empty chunk the total again. The
    /// header length counts only the data after the offset varint (what <see cref="Decode"/> expects).</summary>
    public static List<byte[]> EncodeChunks(byte command, byte sequence, ReadOnlySpan<byte> data, int maxData = 56)
    {
        var list = new List<byte[]>();
        static byte[] One(byte cmd, byte opt, byte seq, int offset, ReadOnlySpan<byte> part)
        {
            Span<byte> lenV = stackalloc byte[4], offV = stackalloc byte[4];
            int ln = WriteVarint(lenV, part.Length), on = WriteVarint(offV, offset);
            var b = new byte[3 + ln + on + part.Length];
            b[0] = cmd; b[1] = opt; b[2] = seq;
            lenV[..ln].CopyTo(b.AsSpan(3)); offV[..on].CopyTo(b.AsSpan(3 + ln)); part.CopyTo(b.AsSpan(3 + ln + on));
            return b;
        }
        for (int at = 0; at < data.Length; at += maxData)
        {
            var part = data.Slice(at, Math.Min(maxData, data.Length - at));
            byte opt = (byte)(OptInternal | OptChunk | (at == 0 ? OptChunkStart | OptAckRequested : 0));
            list.Add(One(command, opt, sequence, at == 0 ? data.Length : at, part));
        }
        list.Add(One(command, (byte)(OptInternal | OptChunk | OptAckRequested), sequence, data.Length, ReadOnlySpan<byte>.Empty));
        return list;
    }

    /// <summary>ACK for one chunk: like <see cref="Ack"/>, with the bytes received so far and the bytes still missing.</summary>
    public static byte[] ChunkAck(in GipMessage m, int received, int remaining) =>
        Encode(CmdAck, OptInternal, m.Sequence, new byte[]
        {
            0x00, m.Command, (byte)(OptInternal | (m.Options & 0x0F)),
            (byte)received, (byte)(received >> 8), 0x00, 0x00, (byte)remaining, (byte)(remaining >> 8)
        });

    /// <summary>Guide (Xbox) button message 0x07: payload[0] bit 0 = pressed.</summary>
    public static bool GuidePressed(ReadOnlySpan<byte> payload) => payload.Length > 0 && (payload[0] & 0x01) != 0;
}
