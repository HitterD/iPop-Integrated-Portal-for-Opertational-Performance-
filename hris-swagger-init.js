
window.onload = function() {
  // Build a system
  let url = window.location.search.match(/url=([^&]+)/);
  if (url && url.length > 1) {
    url = decodeURIComponent(url[1]);
  } else {
    url = window.location.origin;
  }
  let options = {
  "swaggerDoc": {
    "openapi": "3.0.0",
    "paths": {
      "/api/v1/health": {
        "get": {
          "operationId": "HealthController_health",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/sync/status": {
        "get": {
          "operationId": "SyncController_status",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/employees/{nik}": {
        "get": {
          "operationId": "EmployeesController_get",
          "parameters": [
            {
              "name": "nik",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/employees": {
        "get": {
          "operationId": "EmployeesController_list",
          "parameters": [
            {
              "name": "dept",
              "required": true,
              "in": "query",
              "schema": {
                "type": "string"
              }
            },
            {
              "name": "status",
              "required": true,
              "in": "query",
              "schema": {
                "type": "string"
              }
            },
            {
              "name": "page",
              "required": true,
              "in": "query",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/auth/lookup": {
        "post": {
          "operationId": "AuthController_lookup",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/LookupDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/auth/verify": {
        "post": {
          "operationId": "AuthController_verify",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/VerifyDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/ping": {
        "get": {
          "operationId": "PingController_ping",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/login": {
        "post": {
          "operationId": "AdminAuthController_login",
          "parameters": [],
          "responses": {
            "201": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/logout": {
        "post": {
          "operationId": "AdminAuthController_logout",
          "parameters": [],
          "responses": {
            "201": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/sync": {
        "get": {
          "operationId": "AdminController_sync",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/employees": {
        "get": {
          "operationId": "AdminController_employees",
          "parameters": [
            {
              "name": "q",
              "required": true,
              "in": "query",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/employees/count": {
        "get": {
          "operationId": "AdminController_employeesCount",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/audit": {
        "get": {
          "operationId": "AdminController_audit",
          "parameters": [
            {
              "name": "from",
              "required": true,
              "in": "query",
              "schema": {
                "type": "string"
              }
            },
            {
              "name": "to",
              "required": true,
              "in": "query",
              "schema": {
                "type": "string"
              }
            },
            {
              "name": "actor",
              "required": true,
              "in": "query",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/api-keys": {
        "post": {
          "operationId": "AdminController_createApiKey",
          "parameters": [],
          "responses": {
            "201": {
              "description": ""
            }
          }
        },
        "get": {
          "operationId": "AdminController_listApiKeys",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      },
      "/api/v1/admin/api-keys/{id}": {
        "delete": {
          "operationId": "AdminController_revokeApiKey",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "number"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          }
        }
      }
    },
    "info": {
      "title": "API/Proxy Gateway",
      "description": "HRIS-Santos replication gateway API",
      "version": "1.0",
      "contact": {}
    },
    "tags": [],
    "servers": [],
    "components": {
      "securitySchemes": {
        "api-key": {
          "type": "apiKey",
          "in": "header",
          "name": "x-api-key"
        }
      },
      "schemas": {
        "LookupDto": {
          "type": "object",
          "properties": {}
        },
        "VerifyDto": {
          "type": "object",
          "properties": {}
        }
      }
    }
  },
  "customOptions": {}
};
  url = options.swaggerUrl || url
  let urls = options.swaggerUrls
  let customOptions = options.customOptions
  let spec1 = options.swaggerDoc
  let swaggerOptions = {
    spec: spec1,
    url: url,
    urls: urls,
    dom_id: '#swagger-ui',
    deepLinking: true,
    presets: [
      SwaggerUIBundle.presets.apis,
      SwaggerUIStandalonePreset
    ],
    plugins: [
      SwaggerUIBundle.plugins.DownloadUrl
    ],
    layout: "StandaloneLayout"
  }
  for (let attrname in customOptions) {
    swaggerOptions[attrname] = customOptions[attrname];
  }
  let ui = SwaggerUIBundle(swaggerOptions)

  if (customOptions.initOAuth) {
    ui.initOAuth(customOptions.initOAuth)
  }

  if (customOptions.authAction) {
    ui.authActions.authorize(customOptions.authAction)
  }
  
  window.ui = ui
}
