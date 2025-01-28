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