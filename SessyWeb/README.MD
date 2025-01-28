# SessyWeb

This code aims to be a better solution for (dis)charging the Sessy batteries in the home.

To use this code you need to create an account and get a SecurityToken from ENTSO-E.

In the AppSettings.json the following must be configured for your personal installation:

    {
        "Logging": {
            "LogLevel": {
                "Default": "Information",
                "Microsoft.AspNetCore": "Warning"
            }
        },
        "AllowedHosts": "*",

        "ENTSO-E:InDomain": "10YNL----------L", // EIC-code voor Nederland
        "ENTSO-E:ResolutionFormat": "PT60M",

        "Kestrel:Certificates:Development:Password": "Your self-signed certificate password for Swagger web front end", // Move to secrets.json
        "ENTSO-E:SecurityToken": "Your securityToken from ENTSO-E", // Move to secrets.json

        "Sessy:Batteries": {
            "Batteries": {
                "1": {
                    "Name": "Battery 1",
                    "UserId": "Dongle user id",         // Move to secrets.json
                    "Password": "Dongle password",      // Move to secrets.json
                    "BaseUrl": "http://192.168.1.xxx",  // IP Address of your first battery
                    // "BaseUrl": "http://host.docker.internal:3001" // Mock server (Mockoon)
                },
                "2": {
                    "Name": "Battery 2",
                    "UserId": "Dongle user id",         // Move to secrets.json
                    "Password": "Dongle password",      // Move to secrets.json
                    "BaseUrl": "http://192.168.1.xxx",  // IP Address of your second battery
                    // "BaseUrl": "http://host.docker.internal:3001" // Mock server (Mockoon)
                },
                "3": {
                    "Name": "Battery 3",
                    "UserId": "Dongle user id",         // Move to secrets.json
                    "Password": "Dongle password",      // Move to secrets.json
                    "BaseUrl": "http://192.168.1.xxx",  // // IP Address of your third battery
                    // "BaseUrl": "http://host.docker.internal:3001" // Mock server (Mockoon)
                }
            }
        }
    },

    "Sessy:Meters": {
    "Endpoints": {
        "P1": {
            "Name": "P1",
            "BaseUrl": "http://192.168.1.240" // Actual P1 Meter
        }
    },

    "Modbus:Endpoints": {
        "Endpoints": {
            "SolarEdge": {
                "IpAddress": "192.168.1.217", // Actual inverter
                "Port": 1502,
                "SlaveId": 1
            }
        }
    }


If you want to join in on GitHub I advice you to put the 'UserId' and 'Password' in the secrets.json of your Visual Studio project.

Here's an example for the secrest.json:

    {
        "Sessy:Batteries:Batteries:1:UserId": "********",
        "Sessy:Batteries:Batteries:1:Password": "********",
        "Sessy:Batteries:Batteries:2:UserId": "********",
        "Sessy:Batteries:Batteries:2:Password": "********",
        "Sessy:Batteries:Batteries:3:UserId": "********",
        "Sessy:Batteries:Batteries:3:Password": "********",
        "Sessy:Meters:Endpoints:P1:UserId": "********",
        "Sessy:Meters:Endpoints:P1:Password": "********",
        "Kestrel:Certificates:Development:Password": "********-****-****-****-*************",
        "ENTSO-E:SecurityToken": "********-*****-****-****-************"
    }

The current implementation is for the following combination of installed items:
- 1 or more Sessy Home Batteries
- 1 Sessy P1 Meter connected to the smart meter.
- 1 SolarEdge inverter of model type SE5K-RW0TEBNN4

To run the application on your desktop during debugging from Visual Studio Professional you will need Docker Desktop which can be found here: https://docs.docker.com/desktop/setup/install/windows-install/

If more people are interested code could be altered to support several more installations.