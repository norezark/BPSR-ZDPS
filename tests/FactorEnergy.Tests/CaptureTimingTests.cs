using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using BPSR_ZDPSLib;
using PacketDotNet;
using SharpPcap;
using WorldNtf = BPSR_ZDPSLib.ServiceMethods.WorldNtf;

static class CaptureTimingTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (int direction in new[] { -1, 1 })
        {
            test($"capture timing: 72 hours of driver clock drift ({direction:+0;-0})", () =>
            {
                using var session = new CaptureSession();
                var start = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
                for (int hour = 0; hour <= 72; hour++)
                {
                    var receivedAt = start.AddHours(hour);
                    var driverTime = receivedAt.AddMilliseconds(direction * hour * 100);
                    session.Receive(driverTime, receivedAt);
                    AssertTime(receivedAt, session.DispatchNext());
                }
            });
        }

        test("capture timing: queued notifications retain their receive time", () =>
        {
            using var session = new CaptureSession();
            var receivedAt = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
            session.Receive(receivedAt.AddSeconds(-4), receivedAt);
            // Dispatch after the asynchronous reassembler has queued the message. A later
            // parser/UI timestamp must not erase genuine time spent waiting in the queue.
            AssertTime(receivedAt, session.DispatchNext());
        });

        test("capture timing: capture-file replay retains its recorded timestamps", () =>
        {
            using var session = new CaptureSession(replay: true);
            var recordedAt = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            session.Receive(recordedAt, recordedAt.AddYears(6));
            AssertTime(recordedAt, session.DispatchNext());
        });
    }

    static void AssertTime(DateTime expected, DateTime actual)
    {
        if (actual != expected || actual.Kind != DateTimeKind.Utc)
            throw new Exception($"Expected UTC arrival {expected:O}, got {actual:O} ({actual.Kind})");
    }

    // Exercise live packet decoding, TCP reassembly, the asynchronous message queue,
    // and notification delivery without opening a capture device or starting a game.
    sealed class CaptureSession : IDisposable
    {
        const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        readonly NetCap capture = new() { TcpReassempler = new() };
        readonly Action<RawCapture, DateTime> receive;
        readonly Action<ReadOnlySpan<byte>, DateTime> parse;
        DateTime? observedAt;
        DateTime? handledAt;
        uint sequence = 1;

        public CaptureSession(bool replay = false)
        {
            receive = typeof(NetCap).GetMethod("ProcessRawPacket", PrivateInstance)!
                .CreateDelegate<Action<RawCapture, DateTime>>(capture);
            parse = typeof(NetCap).GetMethod("ParsePacket", PrivateInstance)!
                .CreateDelegate<Action<ReadOnlySpan<byte>, DateTime>>(capture);
            capture.TcpReassempler.OnNewConnection = typeof(NetCap).GetMethod("OnNewConnection", PrivateInstance)!
                .CreateDelegate<Action<TcpReassembler.TcpConnection>>(capture);
            capture.ConnectionFilters[new("192.0.2.1", 12000, "192.0.2.2", 13000)] = true;
            typeof(NetCap).GetField("IsDebugCaptureFileMode", PrivateInstance)!.SetValue(capture, replay);
            capture.NotifyObserved += (_, _, extra) => observedAt = extra.ArrivalTime;
            capture.RegisterWorldNotifyHandler(WorldNtf.SyncServerSkillEnd, (_, extra) => handledAt = extra.ArrivalTime);
        }

        public void Receive(DateTime driverTime, DateTime receivedAt)
        {
            var notification = new byte[22];
            BinaryPrimitives.WriteUInt32BigEndian(notification, (uint)notification.Length);
            BinaryPrimitives.WriteUInt16BigEndian(notification.AsSpan(4), (ushort)MsgTypeId.Notify);
            BinaryPrimitives.WriteUInt64BigEndian(notification.AsSpan(6), (ulong)EServiceId.WorldNtf);
            BinaryPrimitives.WriteUInt32BigEndian(notification.AsSpan(18), (uint)WorldNtf.SyncServerSkillEnd);
            var tcp = new TcpPacket(12000, 13000) { SequenceNumber = sequence, PayloadData = notification };
            sequence += (uint)notification.Length;
            var ip = new IPv4Packet(IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2")) { PayloadPacket = tcp };
            var ethernet = new EthernetPacket(PhysicalAddress.Parse("001122334455"),
                PhysicalAddress.Parse("001122334466"), EthernetType.IPv4) { PayloadPacket = ip };
            receive(new RawCapture(LinkLayers.Ethernet, new PosixTimeval(driverTime), ethernet.Bytes, null), receivedAt);
        }

        public DateTime DispatchNext()
        {
            RawPacket? packet = null;
            if (!SpinWait.SpinUntil(() => capture.RawPacketQueue.TryDequeue(out packet), TimeSpan.FromSeconds(5)))
                throw new Exception("TCP reassembly did not deliver the notification");
            try
            {
                observedAt = handledAt = null;
                parse(packet!.Data.AsSpan(0, packet.Len), packet.LastPacketTime);
                if (observedAt == null || handledAt == null)
                    throw new Exception("Notification did not reach both the observer and registered handler");
                AssertTime(observedAt.Value, handledAt.Value);
                return handledAt.Value;
            }
            finally
            {
                packet!.Return();
                capture.RawPacketPool.Return(packet);
            }
        }

        public void Dispose()
        {
            capture.Stop();
            foreach (var connection in capture.TcpReassempler.Connections.Values)
                capture.TcpReassempler.RemoveConnection(connection);
        }
    }
}
