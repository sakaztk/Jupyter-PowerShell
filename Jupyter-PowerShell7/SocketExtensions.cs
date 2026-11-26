using System;
using System.Linq;
using System.Text;
using Jupyter_PowerShell7.Models;
using NetMQ;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jupyter_PowerShell7
{
    public static class SocketExtensions
    {
        private static bool SignaturesEqual(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);
            var different = leftBytes.Length ^ rightBytes.Length;
            var length = Math.Max(leftBytes.Length, rightBytes.Length);
            for (var i = 0; i < length; i++)
            {
                var leftByte = i < leftBytes.Length ? leftBytes[i] : (byte)0;
                var rightByte = i < rightBytes.Length ? rightBytes[i] : (byte)0;
                different |= leftByte ^ rightByte;
            }
            return different == 0;
        }

        private static readonly byte[] Delimiter = Encoding.ASCII.GetBytes("<IDS|MSG>");

        public static Message ReceiveMessage(this IReceivingSocket socket, Kernel kernel = null)
        {
            var message = new Message();
            while (true)
            {
                var bytes = socket.ReceiveFrameBytes();
                if (bytes.SequenceEqual(Delimiter))
                {
                    break;
                }

                message.Identities.Add(bytes);
            }

            message.Signature = socket.ReceiveFrameString();
            var header = socket.ReceiveFrameString();
            var parentHeader = socket.ReceiveFrameString();
            var metadata = socket.ReceiveFrameString();
            var content = socket.ReceiveFrameString();
            if (kernel != null)
            {
                var sig = kernel.SignMessage(header, parentHeader, metadata, content);
                if (!SignaturesEqual(sig, message.Signature ?? string.Empty))
                {
                    Console.WriteLine("! WARNING: Invalid message signature, ignoring the message");
                    message.Signature = null;
                }
            }

            try
            {
                message.Header = JsonConvert.DeserializeObject<Header>(header) ?? new Header();
                message.ParentHeader = JsonConvert.DeserializeObject<Header>(parentHeader) ?? new Header();
                message.MetaData = JObject.Parse(metadata);
                message.Content = JObject.Parse(content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"! WARNING: Invalid Jupyter message JSON: {ex.Message}");
                message.Header = new Header();
                message.ParentHeader = new Header();
                message.MetaData = new JObject();
                message.Content = new JObject();
                message.Signature = null;
            }
            // Console.WriteLine($"> [{message.Header.MessageType}] {metadata} {content}");
            return message;
        }

        public static void SendMessage(this IOutgoingSocket socket, Message message, Kernel kernel = null)
        {
            var header = JsonConvert.SerializeObject(message.Header);
            var parentHeader = JsonConvert.SerializeObject(message.ParentHeader);
            var metadata = JsonConvert.SerializeObject(message.MetaData);
            var content = JsonConvert.SerializeObject(message.Content);

            if (string.IsNullOrEmpty(message.Signature) && kernel != null)
            {
                message.Signature = kernel.SignMessage(header, parentHeader, metadata, content);
            }

            // Console.WriteLine($"< [{message.Header.MessageType}] {metadata} {content}");
            foreach (var identity in message.Identities)
            {
                socket.SendFrame(identity, true);
            }

            socket.SendFrame(Delimiter, true);
            socket.SendFrame(message.Signature, true);
            socket.SendFrame(header, true);
            socket.SendFrame(parentHeader, true);
            socket.SendFrame(metadata, true);
            socket.SendFrame(content);
        }
    }
}