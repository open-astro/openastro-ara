#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Interfaces.Mediator {

    // §8.1 mediator → service migration: WPF VM constraint dropped per playbook
    // ("UI VMs are dropped wholesale"). THandler is now an unconstrained type
    // parameter — concrete handler implementations live in the headless server
    // (e.g., AlpacaCameraDriver implements ICameraVM-equivalent surface).
    // Filename + interface name kept as *Mediator for now to minimize the
    // cross-file rename surface area; the *Service rename is a follow-up sweep.
    public interface IDeviceMediator<THandler, TConsumer, TInfo> where TConsumer : IDeviceConsumer<TInfo> {

        void RegisterHandler(THandler handler);

        void RegisterConsumer(TConsumer consumer);

        void RemoveConsumer(TConsumer consumer);

        Task<IList<string>> Rescan();

        Task<bool> Connect();

        Task Disconnect();

        void Broadcast(TInfo deviceInfo);

        TInfo GetInfo();

        string Action(string actionName, string actionParameters);

        string SendCommandString(string command, bool raw = true);

        bool SendCommandBool(string command, bool raw = true);

        void SendCommandBlind(string command, bool raw = true);

        IDevice GetDevice();

        event Func<object, EventArgs, Task> Connected;
        event Func<object, EventArgs, Task> Disconnected;
    }
}