﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using System;
using System.Collections;
using System.Diagnostics;
#if !FULLYMANAGED
using System.Security.Cryptography.X509Certificates;
#endif
using System.Text;
using System.Threading;

namespace nanoFramework.Azure.Devices.Provisioning.Client
{
    /// <summary>
    /// Allows devices to use the Device Provisioning Service.
    /// </summary>
    public class ProvisioningDeviceClient : IDisposable
    {
        const string DpsSubscription = "$dps/registrations/res/#";
#if FULLYMANAGED
        private IMqttClient _mqttc;
#else
        private MqttClient _mqttc;
#endif
        private readonly string _deviceEndPoint;
        private long _requestId;
        private string _registrationId;
        private int _retry = -1;
        private DateTime _retryInitTime;
        private string _operationId;
        private ProvisioningRegistrationStatusType _status = ProvisioningRegistrationStatusType.Unassigned;
        private ProvisioningRegistrationStatusType _statusLast = ProvisioningRegistrationStatusType.Unassigned;
        private DeviceRegistrationResult _result = null;
        private bool _isMessageProcessed = false;
        private bool _isDisposed = false;
        private string _message = null;

        /// <summary>
        /// Creates an instance of the Device Provisioning Client.
        /// </summary>
        /// <param name="globalDeviceEndpoint">The GlobalDeviceEndpoint for the Device Provisioning Service.</param>
        /// <param name="idScope">The IDScope for the Device Provisioning Service.</param>
        /// <param name="registrationId">The registration ID</param>
        /// <param name="securityProvider">The security provider instance.</param>
        /// <param name="azureCert">The Azure root certificate, leave it null if you have it stored in the device.</param>
#if FULLYMANAGED
        /// <param name="mqtt">The MQTT client instance.</param>
#endif
        /// <returns>An instance of the ProvisioningDeviceClient</returns>
        public static ProvisioningDeviceClient Create(
            string globalDeviceEndpoint,
            string idScope, string registrationId,
            string securityProvider,
#if FULLYMANAGED
            byte[] azureCert = null
#else
            X509Certificate azureCert = null
#endif

#if FULLYMANAGED
            , IMqttClient mqtt = null
#endif
            )
        {
            return new ProvisioningDeviceClient(globalDeviceEndpoint, idScope, registrationId, securityProvider, null, azureCert
#if FULLYMANAGED
                , mqtt
#endif
                );
        }

        /// <summary>
        /// Creates an instance of the Device Provisioning Client.
        /// </summary>
        /// <param name="globalDeviceEndpoint">The GlobalDeviceEndpoint for the Device Provisioning Service.</param>
        /// <param name="idScope">The IDScope for the Device Provisioning Service.</param>
        /// <param name="registrationId">The registration ID</param>
        /// <param name="securityProvider">The security provider instance.</param>
        /// <param name="azureCert">The Azure root certificate, leave it null if you have it stored in the device.</param>
#if FULLYMANAGED
        /// <param name="mqtt">The MQTT client instance.</param>
#endif
        /// <returns>An instance of the ProvisioningDeviceClient</returns>
        public static ProvisioningDeviceClient Create(
            string globalDeviceEndpoint,
            string idScope, string registrationId,
#if FULLYMANAGED
            byte[] securityProvider, 
            byte[] azureCert = null
#else
            X509Certificate securityProvider,
            X509Certificate azureCert = null
#endif

#if FULLYMANAGED
            , IMqttClient mqtt = null
#endif
            )
        {
            return new ProvisioningDeviceClient(globalDeviceEndpoint, idScope, registrationId, null, securityProvider, azureCert
#if FULLYMANAGED
                , mqtt
#endif
                );
        }

        private ProvisioningDeviceClient(string globalDeviceEndpoint, string idScope, string registrationId, string securityProvider,
#if FULLYMANAGED
            byte[] deviceCert, 
            byte[] azureCert
#else
            X509Certificate deviceCert,
            X509Certificate azureCert
#endif
#if FULLYMANAGED
            , IMqttClient mqtt
#endif
            )
        {
            _registrationId = registrationId;
            _deviceEndPoint = globalDeviceEndpoint;

#if FULLYMANAGED
            _mqttc = mqtt;
            _mqttc.Init(
               _deviceEndPoint,
               8883,
               true,
               azureCert,
               deviceCert,
               MqttSslProtocols.TLSv1_2);
#else
            _mqttc = new MqttClient(
               _deviceEndPoint,
               8883,
               true,
               azureCert,
               deviceCert,
               MqttSslProtocols.TLSv1_2);
#endif
            string userName = $"{idScope}/registrations/{_registrationId}/api-version=2019-03-31";

            Helper.ComposeTelemetryInformation(ref userName);

            // Handler for received messages on the subscribed topics
            _mqttc.MqttMsgPublishReceived += ClientMqttMsgReceived;

            // Now connect the device
            string key = securityProvider != null ? Helper.GetSharedAccessSignature(null, securityProvider, $"{idScope}/registrations/{_registrationId}", new TimeSpan(24, 0, 0)) : string.Empty;
            _mqttc.Connect(
                _registrationId,
                userName,
                key,
                false,
                MqttQoSLevel.ExactlyOnce,
                false, null,
                "Disconnected",
                true,
                60
                );

            if (_mqttc.IsConnected)
            {
                _mqttc.Subscribe(
                    new[] {
                        DpsSubscription
                    },
                    new[] {
                        MqttQoSLevel.AtLeastOnce
                    }
                );

                _requestId = DateTime.UtcNow.ToUnixTimeSeconds();
            }
        }

        /// <summary>
        /// Registers the current device using the Device Provisioning Service and assigns it to an IoT Hub.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The registration result.</returns>
        public DeviceRegistrationResult Register(CancellationToken cancellationToken) => Register(null, cancellationToken);

        /// <summary>
        /// Registers the current device using the Device Provisioning Service and assigns it to an IoT Hub.
        /// </summary>
        /// <param name="data">The custom content.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The registration result.</returns>
        public DeviceRegistrationResult Register(ProvisioningRegistrationAdditionalData data, CancellationToken cancellationToken)
        {
            if (!_mqttc.IsConnected)
            {
                return new DeviceRegistrationResult(_registrationId, DateTime.UtcNow, null, null, ProvisioningRegistrationStatusType.Failed, null, DateTime.UtcNow, -1, "MQTT Client is not open", null); ;
            }

            string registration;
            if ((data != null) && (!string.IsNullOrEmpty(data.JsonData)))
            {
                registration = $"{{\"registrationId\":\"{_registrationId}\",\"payload\":{data.JsonData}}}";
            }
            else
            {
                registration = $"{{\"registrationId\":\"{_registrationId}\"}}";
            }

            _mqttc.Publish($"$dps/registrations/PUT/iotdps-register/?$rid={_requestId}", Encoding.UTF8.GetBytes(registration));

            while ((!cancellationToken.IsCancellationRequested) && (_status != ProvisioningRegistrationStatusType.Assigned))
            {
                // We force to clean the memory at this stage, for constrained devices
                Runtime.Native.GC.Run(true);
                Thread.Sleep(200);
                // Don't ask for a new message if we are already processing one
                if ((_retry > 0) && (!_isMessageProcessed))
                {
                    if (_retryInitTime < DateTime.UtcNow)
                    {
                        _mqttc.Publish($"$dps/registrations/GET/iotdps-get-operationstatus/?$rid={_requestId}&operationId={_operationId}", null);
                        _retryInitTime = DateTime.UtcNow.AddSeconds(_retry);
                    }
                }
            }

            CleanAll();

            // Creating a DeviceRegistrationResult only when it's needed
            // One more time cleaning the memory for small devices, a bit of performances penalties
            Runtime.Native.GC.Run(true);
            if (!string.IsNullOrEmpty(_message))
            {
                var opeStat = (RegistrationOperationStatus)nanoFramework.Json.JsonConvert.DeserializeObject(_message, typeof(RegistrationOperationStatus));
                var reg = opeStat.registrationState;
                var status = AssignStatus(opeStat.status);
                if (_statusLast == ProvisioningRegistrationStatusType.Assigned)
                {
                    ProvisioningRegistrationSubstatusType sub = ProvisioningRegistrationSubstatusType.InitialAssignment;
                    if (reg.substatus == "deviceDataMigrated")
                    {
                        sub = ProvisioningRegistrationSubstatusType.DeviceDataMigrated;
                    }
                    else if (reg.substatus == "deviceDataReset")
                    {
                        sub = ProvisioningRegistrationSubstatusType.DeviceDataReset;
                    }

                    _result = new(reg.registrationId, reg.createdDateTimeUtc, reg.assignedHub, reg.deviceId, status, sub, string.Empty, reg.lastUpdatedDateTimeUtc, reg.errorCode, reg.errorMessage, reg.etag, opeStat.registrationState.payload);
                }
                else
                {
                    _result = new(reg.registrationId, reg.createdDateTimeUtc, reg.assignedHub, reg.deviceId, status, string.Empty, reg.lastUpdatedDateTimeUtc, reg.errorCode, reg.errorMessage, reg.etag);
                }
            }

            return _result ?? new DeviceRegistrationResult(_registrationId, DateTime.UtcNow, string.Empty, string.Empty, _status, string.Empty, DateTime.UtcNow, -1, $"Unknown error, cancellation requested: {cancellationToken.IsCancellationRequested}", string.Empty);
        }

        private void CleanAll()
        {
            // We need to clean everything now
            _mqttc.MqttMsgPublishReceived -= ClientMqttMsgReceived;
            try
            {
                _mqttc.Unsubscribe(new[] {
                        DpsSubscription
                    });
            }
            catch
            {
                // Nothing on purpose, just cleaning
            }

            _mqttc.Disconnect();
            while (_mqttc.IsConnected)
            {
                Thread.Sleep(100);
            }

            // We do have to wait for the mqtt client to send everything
            // And then fully clean it to avoid issues with the SSL
            GC.SuppressFinalize(_mqttc);
            _mqttc = null;
        }

        private void ClientMqttMsgReceived(object sender, MqttMsgPublishEventArgs e)
        {
            try
            {
                string message = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);
                Debug.WriteLine(e.Topic);
                Debug.WriteLine(message);
                ProvisioningRegistrationStatusType status = _status;
                // The message after the publish looks like that:
                // $dps/registrations/res/202/?$rid={request_id}&retry-after=x
                // x is the retry-after value in seconds
                var opeStat = (RegistrationOperationStatusSimple)Json.JsonConvert.DeserializeObject(message, typeof(RegistrationOperationStatusSimple));
                if (e.Topic.StartsWith($"$dps/registrations/res/202/?$rid={_requestId}"))
                {
                    _operationId = opeStat.operationId;
                    status = AssignStatus(opeStat.status);
                    _retry = Convert.ToInt32(e.Topic.Substring(e.Topic.LastIndexOf('=') + 1));
                    _retryInitTime = DateTime.UtcNow.AddSeconds(_retry);
                }
                else if ((e.Topic.StartsWith($"$dps/registrations/res/200/?$rid={_requestId}")) && (!_isMessageProcessed))
                {
                    // This is to avoid having multiple messages to process
                    _isMessageProcessed = true;
                    _operationId = opeStat.operationId;
                    status = AssignStatus(opeStat.status);
                    _message = message;
                    _statusLast = _status;
                }
                else if (!_isMessageProcessed)
                {
                    // This is an error and we will have an error response
                    Hashtable error = (Hashtable)Json.JsonConvert.DeserializeObject(message, typeof(Hashtable));
                    _result = new(_registrationId, DateTime.UtcNow, string.Empty, string.Empty, ProvisioningRegistrationStatusType.Failed, string.Empty, DateTime.UtcNow, (int)error["errorCode"], (string)error["message"], string.Empty);
                    // Forcing the loop to stop
                    status = ProvisioningRegistrationStatusType.Assigned;
                }

                _status = status;
            }
            catch (Exception ex)
            {
                // Just don't do anything, let's give it another try
                Debug.WriteLine($"Exception in event: {ex}");
            }
        }

        private ProvisioningRegistrationStatusType AssignStatus(string status)
        {
            if (status == RegistrationOperationStatus.Assigned)
            {
                return ProvisioningRegistrationStatusType.Assigned;
            }
            else if (status == RegistrationOperationStatus.Assigning)
            {
                return ProvisioningRegistrationStatusType.Assigning;
            }
            else if (status == RegistrationOperationStatus.Unassigned)
            {
                return ProvisioningRegistrationStatusType.Unassigned;
            }
            else if (status == RegistrationOperationStatus.Failed)
            {
                return ProvisioningRegistrationStatusType.Failed;
            }
            else if (status == RegistrationOperationStatus.Disabled)
            {
                return ProvisioningRegistrationStatusType.Disabled;
            }

            // That should never happen but in case
            return ProvisioningRegistrationStatusType.Failed;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_mqttc != null)
            {
                CleanAll();
            }
        }
    }
}
