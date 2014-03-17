Asterisk Relay
==============


Description
-----------
This service allows you to control multiple Conrad relay boards via Asterisk
and any WSDL/WCF service.

It provides named virtual switches that can be combined in a Boolean function
and mapped to physical relays. The switch states are synchronized with device
states on Asterisk thus enabling the use of BLFs (Blinking Light Fields).
The exposed web service allows clients to enumerate the switches and to get and
set their individual state. Switches set in that manner are of course synced
with Asterisk too.


Configuration
-------------
In the following dial plan and configuration examples, we assume the following
simple scenario:

* There are three physical lights in our made-up home that should be controlled
  by the system: one in the west wing, one in the east wing and one in the
  entrance area, connected to port #1 to #3 on a single Conrad board in that
  order on COM1.
* Each light must be able to be controlled separately, while the one in the
  entrance area should also be on if either the one in the left or right wing
  is turned on.
* The names of the virtual switches are *West*, *East* and *Entrance*, the
  extensions that are used to register the BLF on the phones are `*3278`,
  `*9378` and `*3678`.
* The Asterisk server's IP address is `192.168.1.1`, *Asterisk Relay* runs on
  `192.168.1.2`.


### Dial Plan
An extension for each virtual switch has to be registered within the dial plan.
This includes a device hint using the custom notification scheme. In order to
ensure that the name of a switch doesn't interfere with another custom device
name, it is prefixed with `Relay`.
The extension itself simply consists of a `UserEvent` that sends an event that
tells the system to toggle the state:

    exten => *3278,hint,Custom:RelayWest
    exten => *3287,1,UserEvent(Relay,Switch: West,Action: Toggle)
    exten => *9378,hint,Custom:RelayEast
    exten => *9378,1,UserEvent(Relay,Switch: East,Action: Toggle)
    exten => *3678,hint,Custom:RelayEntrance
    exten => *3678,1,UserEvent(Relay,Switch: Entrance,Action: Toggle)

Besides `Toggle` the actions `TurnOn` and `TurnOff` are also available.


### Asterisk Manager
The Asterisk Manager over HTTP needs to be enabled. Since this can be done in
quite numerous fashion - e.g. forwarding requests from Apache - the following
configuration example simply enabled the service without any prefix.

#### *http.conf*

    [general]
    enabled=yes
    prefix=

#### *manager.conf*

    [general]
    enabled=yes
    webenabled=yes

    [relay]
    secret=P@ssw0rd
    deny=0.0.0.0/0.0.0.0
    permit=192.168.1.2/255.255.255.255
    read=user
    write=call

The second section registers a user named `relay` and permits access only from
the *Asterisk Relay* server and only to the required read and write classes.


### Service
All configuration of the *Asterisk Relay* service is done thru its app config
file `AsteriskRelay.exe.config`. The different sections are explained in the
following paragraphs.

#### Switches
The following snippet registers the available switches. By default, switches
are turned off at startup and then - if available - set to the state stored on
Asterisk. The switch `Entrance` is configured to be `On` at startup.

    <setting name="Switches" serializeAs="Xml">
      <value>
        <ArrayOfSwitch>
          <Switch><Name>West</Name></Switch>
          <Switch><Name>East</Name></Switch>
          <Switch>
            <Name>Entrance</Name>
            <State>On</State>
          </Switch>
        </ArrayOfSwitch>
      </value>
    </setting>

#### Boards and Relays
According to the aforementioned requirements, we end up with the following
board configuration:

    <setting name="Boards" serializeAs="Xml">
      <value>
        <ArrayOfBoard xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Board>
            <Port>COM1</Port>
            <Address>1</Address>
            <Relay1 xsi:type="SwitchRef"><Name>West</Name></Relay1>
            <Relay2 xsi:type="SwitchRef"><Name>East</Name></Relay2>
            <Relay3 xsi:type="Or">
              <Op1 xsi:type="SwitchRef"><Name>Entrance</Name></Op1>
              <Op2 xsi:type="Or">
                <Op1 xsi:type="SwitchRef"><Name>West</Name></Op1>
                <Op2 xsi:type="SwitchRef"><Name>East</Name></Op2>
              </Op2>
            </Relay3>
            <Relay4 xsi:type="AlwaysOff"/>
            <Relay5 xsi:type="AlwaysOff"/>
            <Relay6 xsi:type="AlwaysOff"/>
            <Relay7 xsi:type="AlwaysOff"/>
            <Relay8 xsi:type="AlwaysOff"/>
          </Board>
        </ArrayOfBoard>
      </value>
    </setting>

It's easy to see how further boards can be registered and how relays are set.
For further information on the different types of Boolean functions that are
available, please refer to documentation in `Configuration.cs`.

#### Manager Connection
This one is straightforward:

    <setting name="AsteriskManagerInterfaces" serializeAs="Xml">
      <value>
        <ArrayOfAsteriskManagerInterface>
          <AsteriskManagerInterface>
            <Hostname>192.168.1.1</Hostname>
            <Port>8088</Port>
            <Prefix></Prefix>
            <Username>relay</Username>
            <Password>P@ssw0rd</Password>
            <DeviceNameFormat>Custom:Relay{0}</DeviceNameFormat>
            <RetryInterval>30000</RetryInterval>
          </AsteriskManagerInterface>
        </ArrayOfAsteriskManagerInterface>
      </value>
    </setting>

Multiple Asterisk connections can be established which allows a single relay
board to be shared among multiple servers, which explains why interfaces are
registered within an array. The `RetryInterval` determines the number of milli-
seconds after which a dropped or faulty connection is re-established.

#### Static Serial Port Settings
Read and write time-outs as well as the retry interval for all serial ports can
be configured. If missing, they are set to infinite and the connection to the
board is only retried upon re-connection the COM port.

    <setting name="SerialPort" serializeAs="Xml">
      <value>
        <SerialPort>
          <ReadTimeout>2000</ReadTimeout>
          <WriteTimeout>2000</WriteTimeout>
          <RetryInterval>10000</RetryInterval>
        </SerialPort>
      </value>
    </setting>

#### Web Service
This part of the configuration is done in the same way as most *WCF* services,
i.e. within `system.serviceModel`. Since this model allows for quite exotic
exported services, we limit our example for a simple WSDL compatible service
that uses default Windows credentials, meaning every user that is allowed to
logon to the *Asterisk Relay* server is also allowed to turn switches on and
off.

    <system.serviceModel>
      <services>
        <service name="Aufbauwerk.Asterisk.Relay.Remoting.Service" behaviorConfiguration="RemotingBehavior">
          <host>
            <baseAddresses>
              <add baseAddress="http://192.168.1.2/Relay"/>
            </baseAddresses>
          </host>
          <endpoint contract="Aufbauwerk.Asterisk.Relay.Remoting.IService" binding="basicHttpBinding" bindingConfiguration="RemotingBinding"/>
        </service>
      </services>
      <behaviors>
        <serviceBehaviors>
          <behavior name="RemotingBehavior">
            <serviceMetadata httpGetEnabled="true"/>
          </behavior>
        </serviceBehaviors>
      </behaviors>
      <bindings>
        <basicHttpBinding>
          <binding name="RemotingBinding">
            <security mode="TransportCredentialOnly">
              <transport clientCredentialType="Windows"/>
            </security>
          </binding>
        </basicHttpBinding>
      </bindings>
    </system.serviceModel>

Depending on the OS version and the way the program is installed, you may need
to [register](http://msdn.microsoft.com/en-us/library/ms733768(v=vs.110).aspx)
the `/Relay` context with `netsh.exe`.


Installation
------------
As any Windows service, *Asterisk Relay* can be installed with any program
capable of managing *SCM*, like `sc.exe`.


Web Service example
------------------
Finally a short example how to control the system from a WSDL client, in this
case *PowerShell*:

    $service = New-WebServiceProxy http://192.168.1.2/Relay -UseDefaultCredential
    foreach ($switch in $service.GetSwitchNames()) {
        $service.SetSwitchState($switch, $false)
    }

A connection is made to the server, the switches are enumerated and turned off.
