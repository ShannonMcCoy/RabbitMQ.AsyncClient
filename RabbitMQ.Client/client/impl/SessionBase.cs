// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 1.1.
//
// The APL v2.0:
//
//---------------------------------------------------------------------------
//   Copyright (c) 2007-2016 Pivotal Software, Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//---------------------------------------------------------------------------
//
// The MPL v1.1:
//
//---------------------------------------------------------------------------
//  The contents of this file are subject to the Mozilla Public License
//  Version 1.1 (the "License"); you may not use this file except in
//  compliance with the License. You may obtain a copy of the License
//  at http://www.mozilla.org/MPL/
//
//  Software distributed under the License is distributed on an "AS IS"
//  basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See
//  the License for the specific language governing rights and
//  limitations under the License.
//
//  The Original Code is RabbitMQ.
//
//  The Initial Developer of the Original Code is Pivotal Software, Inc.
//  Copyright (c) 2007-2016 Pivotal Software, Inc.  All rights reserved.
//---------------------------------------------------------------------------

using System;
using RabbitMQ.Client.Exceptions;
using RabbitMQ.Client.Framing.Impl;
using System.Collections.Generic;
using System.Threading.Tasks;
using RabbitMQ.Client.Events;

namespace RabbitMQ.Client.Impl
{
    public abstract class SessionBase : ISession
    {
        private readonly object _shutdownLock = new object();
        private AsyncEventHandler<ShutdownEventArgs> _sessionShutdown;

        public SessionBase(Connection connection, int channelNumber)
        {
            CloseReason = null;
            Connection = connection;
            ChannelNumber = channelNumber;
            if (channelNumber != 0)
            {
                connection.ConnectionShutdown += OnConnectionShutdown;
            }
        }

        public event AsyncEventHandler<ShutdownEventArgs> SessionShutdown
        {
            add
            {
                bool ok = false;
                if (CloseReason == null)
                {
                    lock (_shutdownLock)
                    {
                        if (CloseReason == null)
                        {
                            _sessionShutdown += value;
                            ok = true;
                        }
                    }
                }
                if (!ok)
                {
                    value(this, CloseReason);
                }
            }
            remove
            {
                lock (_shutdownLock)
                {
                    _sessionShutdown -= value;
                }
            }
        }

        public int ChannelNumber { get; private set; }
        public ShutdownEventArgs CloseReason { get; set; }
        public Func<ISession, Command, Task> CommandReceived { get; set; }
        public Connection Connection { get; private set; }

        public bool IsOpen
        {
            get { return CloseReason == null; }
        }

        IConnection ISession.Connection
        {
            get { return Connection; }
        }

        public virtual async Task OnCommandReceived(Command cmd)
        {
            if (CommandReceived != null)
            {
                await CommandReceived(this, cmd);
            }
        }

        public virtual Task OnConnectionShutdown(object conn, ShutdownEventArgs reason)
        {
            return Close(reason);
        }

        public virtual async Task OnSessionShutdown(ShutdownEventArgs reason)
        {
            Connection.ConnectionShutdown -= OnConnectionShutdown;
            AsyncEventHandler<ShutdownEventArgs> handler;
            lock (_shutdownLock)
            {
                handler = _sessionShutdown;
                _sessionShutdown = null;
            }
            if (handler != null)
            {
                await handler(this, reason);
            }
        }

        public override string ToString()
        {
            return GetType().Name + "#" + ChannelNumber + ":" + Connection;
        }

        public Task Close(ShutdownEventArgs reason)
        {
            return Close(reason, true);
        }

        public async Task Close(ShutdownEventArgs reason, bool notify)
        {
            if (CloseReason == null)
            {
                lock (_shutdownLock)
                {
                    if (CloseReason == null)
                    {
                        CloseReason = reason;
                    }
                }
            }
            if (notify)
            {
                await OnSessionShutdown(CloseReason);
            }
        }

        public abstract Task HandleFrame(InboundFrame frame);

        public async Task Notify()
        {
            // Ensure that we notify only when session is already closed
            // If not, throw exception, since this is a serious bug in the library
            if (CloseReason == null)
            {
                lock (_shutdownLock)
                {
                    if (CloseReason == null)
                    {
                        throw new Exception("Internal Error in Session.Close");
                    }
                }
            }
            await OnSessionShutdown(CloseReason);
        }

        public virtual Task Transmit(Command cmd)
        {
            if (CloseReason != null)
            {
                lock (_shutdownLock)
                {
                    if (CloseReason != null)
                    {
                        if (!Connection.Protocol.CanSendWhileClosed(cmd))
                        {
                            throw new AlreadyClosedException(CloseReason);
                        }
                    }
                }
            }
            // We used to transmit *inside* the lock to avoid interleaving
            // of frames within a channel.  But that is fixed in socket frame handler instead, so no need to lock.
            return cmd.Transmit(ChannelNumber, Connection);
        }
        public virtual Task Transmit(IList<Command> commands)
        {
            return Connection.WriteFrameSet(Command.CalculateFrames(ChannelNumber, Connection, commands));
        }
    }
}
