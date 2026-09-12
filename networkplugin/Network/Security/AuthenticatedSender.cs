using System;

namespace NetworkPlugin.Network.Security
{
        public readonly struct AuthenticatedSender : IEquatable<AuthenticatedSender>
    {
                public int ConnectionId { get; }

                public string PlayerId { get; }

                public string RoomId { get; }

                public bool IsHost { get; }

                public string SessionNonce { get; }

        public AuthenticatedSender(int connectionId, string playerId, string roomId, bool isHost, string sessionNonce = "")
        {
            ConnectionId = connectionId;
            PlayerId = playerId ?? string.Empty;
            RoomId = roomId ?? string.Empty;
            IsHost = isHost;
            SessionNonce = sessionNonce ?? string.Empty;
        }

                public static readonly AuthenticatedSender Anonymous = new(-1, string.Empty, string.Empty, false, string.Empty);

                public static readonly AuthenticatedSender LocalHost = new(0, "__local_host__", string.Empty, true, string.Empty);

        public bool Equals(AuthenticatedSender other)
        {
            return ConnectionId == other.ConnectionId &&
                   string.Equals(PlayerId, other.PlayerId, StringComparison.Ordinal) &&
                   string.Equals(RoomId, other.RoomId, StringComparison.Ordinal) &&
                   IsHost == other.IsHost &&
                   string.Equals(SessionNonce, other.SessionNonce, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is AuthenticatedSender other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + ConnectionId;
                hash = hash * 31 + (PlayerId != null ? StringComparer.Ordinal.GetHashCode(PlayerId) : 0);
                hash = hash * 31 + (RoomId != null ? StringComparer.Ordinal.GetHashCode(RoomId) : 0);
                hash = hash * 31 + IsHost.GetHashCode();
                hash = hash * 31 + (SessionNonce != null ? StringComparer.Ordinal.GetHashCode(SessionNonce) : 0);
                return hash;
            }
        }

        public static bool operator ==(AuthenticatedSender left, AuthenticatedSender right) => left.Equals(right);
        public static bool operator !=(AuthenticatedSender left, AuthenticatedSender right) => !left.Equals(right);

        public override string ToString() => $"[Sender: Id={PlayerId}, Conn={ConnectionId}, Host={IsHost}, Room={RoomId}]";
    }
}
