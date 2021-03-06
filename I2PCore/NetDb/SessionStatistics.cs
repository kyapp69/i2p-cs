﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using I2PCore.Data;
using System.Threading;
using I2PCore.Utils;
using System.Diagnostics;
using System.Net;

namespace I2PCore
{
    public class MTUConfig
    {
        public const int BufferSize = 1484 - 28;

        public int MTU;
        public int MTUMax;
        public int MTUMin;
    }

    public interface IMTUProvider
    {
        MTUConfig GetMTU( IPEndPoint ep );
        void MTUUsed( IPEndPoint ep, MTUConfig mtu );
    }

    public class SessionStatistics: IMTUProvider
    {
        enum StoreRecordId : int { DestinationStatistics = 1 };
        Dictionary<I2PIdentHash, DestinationStatistics> Destinations = new Dictionary<I2PIdentHash, DestinationStatistics>();

        public DestinationStatistics this[ I2PIdentHash ix ]
        {
            get
            {
                lock ( Destinations )
                {
                    DestinationStatistics stat;

                    if ( !Destinations.ContainsKey( ix ) )
                    {
                        stat = new DestinationStatistics( ix );
                        Destinations[ix] = stat;
                    }
                    else
                    {
                        stat = Destinations[ix];
                    }
                    return stat;
                }
            }
        }

        private Store GetStore()
        {
            return new Store( NetDb.Inst.GetFullPath( "statistics.sto" ), -1 );
        }

        public void Load()
        {
            using ( var s = GetStore() )
            {
                var readsw = new Stopwatch();
                var constrsw = new Stopwatch();
                var dicsw = new Stopwatch();
                var sw2 = new Stopwatch();
                sw2.Start();
                var ix = 0;
                while ( ( ix = s.Next( ix ) ) > 0 )
                {
                    readsw.Start();
                    var data = s.Read( ix );
                    readsw.Stop();

                    var reader = new BufRefLen( data );
                    switch ( (StoreRecordId)reader.Read32() )
                    {
                        case StoreRecordId.DestinationStatistics:
                            constrsw.Start();
                            var one = new DestinationStatistics( reader );
                            constrsw.Stop();

                            dicsw.Start();
                            Destinations[one.Id] = one;
                            dicsw.Stop();

                            one.StoreIx = ix;
                            break;

                        default:
                            s.Delete( ix );
                            break;
                    }
                }
                sw2.Stop();
                Logging.Log( $"Statistics load: Total: {sw2.Elapsed}, " + 
                    $"Read(): {readsw.Elapsed}, Constr: {constrsw.Elapsed}, " + 
                    $"Dict: {dicsw.Elapsed} " );

                // var times = Destinations.Select( d => d.Value.TunnelBuildTimeMsPerHop.ToString() );
                // System.IO.File.WriteAllLines( "/tmp/ct.txt", times );
            }
        }

        const float twoweeks = 1000 * 60 * 60 * 24 * 14;

        public void Save()
        {
            var sw2 = new Stopwatch();
            sw2.Start();
            var deleted = 0;
            var updated = 0;
            var created = 0;
            using ( var s = GetStore() )
            {
                lock ( Destinations )
                {
                    if ( !Destinations.Any() ) return;

                    foreach ( var one in Destinations.ToArray() )
                    {
                        if ( one.Value.Deleted && one.Value.StoreIx > 0 )
                        {
                            s.Delete( one.Value.StoreIx );
                            one.Value.StoreIx = -1;

                            Destinations.Remove( one.Key );
                            ++deleted;
                            continue;
                        }

                        var rec = new BufLen[] { (BufLen)(int)StoreRecordId.DestinationStatistics, new BufLen( one.Value.ToByteArray() ) };

                        if ( one.Value.StoreIx > 0 )
                        {
                            if ( one.Value.Updated )
                            {
                                s.Write( rec, one.Value.StoreIx );
                                ++updated;
                                one.Value.Updated = false;
                            }
                        }
                        else
                        {
                            one.Value.StoreIx = s.Write( rec );
                            ++created;
                        }
                    }
                }
            }
            sw2.Stop();
            Logging.Log( $"Statistics save: {sw2.Elapsed}, {created} created, " +
                $"{updated} updated, {deleted} deleted." );
        }

        public delegate void Accessor( DestinationStatistics ds );

        public void Update( I2PIdentHash target, Accessor acc, bool success )
        {
            var rec = this[target];
            rec.Updated = true;
            if ( success ) rec.LastSeen = I2PDate.Now;
            acc( rec );
        }

        public void SuccessfulConnect( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.SuccessfulConnects ), true );
        }

        public void FailedToConnect( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.FailedConnects ), false );
        }

        public void DestinationInformationFaulty( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.InformationFaulty ), false );
        }

        public void SlowHandshakeConnect( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.SlowHandshakeConnect ), false );
        }

        public void SuccessfulTunnelMember( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.SuccessfulTunnelMember ), true );
        }

        public void MaxBandwidth( I2PIdentHash hash, Bandwidth bw )
        {
            Update( hash, ds => ds.MaxBandwidthSeen = Math.Max( ds.MaxBandwidthSeen, bw.BitrateMax ), false );
        }

        public void DeclinedTunnelMember( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.DeclinedTunnelMember ), false );
        }

        public void SuccessfulTunnelTest( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.SuccessfulTunnelTest ), true );
        }

        public void FailedTunnelTest( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.FailedTunnelTest ), false );
        }

        // Not answering a build request is wose than declining
        public void TunnelBuildTimeout( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.TunnelBuildTimeout ), false );
        }

#if DEBUG
        float msph = 10000;
        PeriodicLogger LogHopBuild = new PeriodicLogger( 30 );
#endif

        public void TunnelBuildTimeMsPerHop( I2PIdentHash hash, long ms )
        {
#if DEBUG
            msph = ( msph * 49f + (float)ms ) / 50f;
            LogHopBuild.Log( () => "Tunnel build time ema: " + msph.ToString( "F0" ) );
#endif
            Update( hash, ds => ds.TunnelBuildTimeMsPerHop = Math.Min( ds.TunnelBuildTimeMsPerHop, ms ), true );
        }

        public void FloodfillUpdateTimeout( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.FloodfillUpdateTimeout ), false );
        }

        public void FloodfillUpdateSuccess( I2PIdentHash hash )
        {
            Update( hash, ds => Interlocked.Increment( ref ds.FloodfillUpdateSuccess ), true );
        }

        public void MTUUsed( IPEndPoint ep, MTUConfig mtu )
        {
        }

        public void UpdateScore()
        {
            lock ( Destinations )
            {
                foreach ( var one in Destinations.ToArray() ) one.Value.UpdateScore();
            }
        }

        bool NodeInactive( 
            DestinationStatistics d, 
            float avg,
            float stddev )
        {
            var result =
                ( d.FailedTunnelTest > 8 && d.FailedTunnelTest > 3 * d.SuccessfulTunnelTest ) ||
                ( d.FailedConnects > 5 && d.FailedConnects > 3 * d.SuccessfulConnects );

            return result;
        }

        IEnumerable<I2PIdentHash> GetInactive( 
            IEnumerable<KeyValuePair<I2PIdentHash,DestinationStatistics>> p ) 
        {
            if ( !p.Any() ) return Enumerable.Empty<I2PIdentHash>();

            var avg = p.Average( d => d.Value.Score );
            var stddev = p.StdDev( d => d.Value.Score );

            return p.Where( d => NodeInactive( d.Value, avg, stddev ) )
                .Select( d => d.Key );
        }

        internal IEnumerable<I2PIdentHash> GetInactive()
        {
            lock ( Destinations )
            {
                if ( !Destinations.Any() ) return Enumerable.Empty<I2PIdentHash>();

                var notff = Destinations.Where( d => d.Value.FloodfillUpdateSuccess == 0
                    && d.Value.FloodfillUpdateTimeout == 0 );

                var ff = Destinations.Where( d => d.Value.FloodfillUpdateSuccess != 0
                    || d.Value.FloodfillUpdateTimeout != 0 );

                return GetInactive( notff )
                    .Concat( GetInactive( ff ) )
                    .ToArray();
            }
        }

        internal void RemoveOldStatistics()
        {
            const ulong oneweek = 1000 * 60 * 60 * 24 * 7;
            var now = (float)(ulong)I2PDate.Now;

            lock ( Destinations )
            {
                var toremove = Destinations.Where( one =>
                    Math.Abs( now - (float)(ulong)one.Value.Created ) > oneweek )
                    .ToArray();

                foreach( var one in toremove )
                {
                    one.Value.Deleted = true;
                }
            }

            Save();
        }

        public void Remove( I2PIdentHash hash )
        {
            lock ( Destinations )
            {
                if ( Destinations.ContainsKey( hash ) )
                {
                    Destinations[hash].Deleted = true;
                }
            }
        }

        public MTUConfig GetMTU( IPEndPoint ep )
        {
            var result = new MTUConfig();

            if ( ep == null )
            {
                result.MTU = 1484 - 28; // IPV4 28 byte UDP header
                result.MTUMax = 1484 - 28;
                result.MTUMin = 620 - 28;
                return result;
            }

            switch ( ep.AddressFamily )
            {
                case System.Net.Sockets.AddressFamily.InterNetwork:
                    result.MTU = 1484 - 28;
                    result.MTUMax = 1484 - 28;
                    result.MTUMin = 620 - 28;
                    break;

                case System.Net.Sockets.AddressFamily.InterNetworkV6:
                    result.MTU = 1280 - 48;  // IPV6 48 byte UDP header
                    result.MTUMax = 1472 - 48;
                    result.MTUMin = 1280 - 48;
                    break;

                default:
                    throw new NotImplementedException( ep.AddressFamily.ToString() + " not supported" );
            }

            return result;
        }
    }
}
