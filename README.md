# envsync

Herramienta de línea de comandos multiplataforma (Windows, Linux y macOS) que conecta con **Azure Key Vault**,
**AWS Secrets Manager** y **HashiCorp Vault** y entrega los secretos a tu programa **sin archivos `.env` en texto plano**.
Además avisa antes de ejecutar si falta alguna llave requerida.

```text
envsync run -- dotnet run                      # los secretos solo existen en el entorno del proceso hijo
envsync env --shell pwsh | Invoke-Expression   # o variables de sesión en tu shell actual
envsync check                                  # ¿está todo lo que necesito? (útil en CI)
```

## Por qué

Un `.env` copiado de máquina en máquina acaba en un chat, en un backup o en un commit. Con envsync el repositorio solo lleva un
manifiesto (`envsync.json`) con **referencias**, y los valores viajan del gestor de secretos a la memoria de tu proceso.

## Instalación

Requiere el SDK o el runtime de **.NET 10**.

```bash
dotnet pack src/EnvSync.Cli -c Release -o artifacts
dotnet tool install --global --add-source ./artifacts EnvSync.Tool
envsync --version
```

## Manifiesto

`envsync.json` se busca hacia arriba desde el directorio actual (como git busca `.git`) o se indica con `--manifest`.
Admite comentarios y comas finales. Hay un ejemplo completo en [`examples/envsync.json`](examples/envsync.json).

```jsonc
{
  "defaultProfile": "dev",
  "profiles": {
    "dev": {
      "providers": {
        "kv":    { "type": "azure-keyvault",  "uri": "https://mi-vault.vault.azure.net" },
        "vault": { "type": "hashicorp-vault", "address": "https://vault.ejemplo.com:8200" }
      },
      "variables": {
        "DB_PASSWORD": { "from": "kv",    "ref": "db-password" },
        "JWT_SECRET":  { "from": "vault", "ref": "app#jwt" },
        "LOG_LEVEL":   { "from": "kv",    "ref": "log-level", "required": false }
      }
    }
  }
}
```

**Referencias (`ref`)**: `nombre` o `nombre#campo`. Con `#campo` se extrae una propiedad de un secreto JSON.

| Proveedor (`type`) | Ajustes | Notas |
|---|---|---|
| `azure-keyvault` | `uri` (https) | El nombre del secreto solo admite letras, dígitos y `-`. `DB_PASSWORD` no existe en Key Vault: el `ref` es el nombre **en la bóveda**. Solo se aceptan hosts de Key Vault (`*.vault.azure.net` y equivalentes de nubes soberanas). |
| `aws-secrets` | `region` (o `AWS_REGION`), `profile` (opcional) | Acepta nombre o ARN. Los secretos binarios no se admiten. Perfiles SSO y de rol soportados. |
| `hashicorp-vault` | `address` (o `VAULT_ADDR`), `namespace`, `mount` (por defecto `secret`), `kv` (`1` o `2`, por defecto `2`) | La ruta es **relativa al `mount`**. Sin `#campo` da error, porque un secreto de Vault tiene varios campos. Ver "A dónde va tu token". |

Los ajustes desconocidos o en blanco son un error, no se ignoran ni caen a un valor por defecto. Todos los errores del manifiesto se
reportan de una vez, cada uno con su ruta JSON.

**Un secreto vacío no es un valor.** Si un secreto existe pero está vacío, cuenta como que falta: una variable requerida falla (código 11).

**Variables opcionales** (`"required": false`): solo se omiten si el secreto **no existe** o está vacío, con una advertencia que
dice por qué. Una credencial rechazada, un timeout, un JSON con forma inesperada o una configuración inválida **siguen siendo error**:
nunca se arranca a medias. Una opcional omitida se **quita** del entorno del hijo, para que no sobreviva un valor heredado de otra sesión.

**Nombres reservados.** Un gestor de secretos no puede fijar variables que cargan código o reconfiguran un shell: `PATH`, `PATHEXT`,
`COMSPEC`, `PSModulePath`, `IFS`, `ENV`, `BASH_ENV`, `PROMPT_COMMAND`, `PS0`-`PS4`, `NODE_OPTIONS`, `PYTHONPATH`, `JAVA_TOOL_OPTIONS`,
`DOTNET_STARTUP_HOOKS`, `GIT_SSH_COMMAND`, cualquiera que empiece por `LD_` o `DYLD_`, entre otras. Sin distinguir mayúsculas.
Dos nombres que solo difieren en mayúsculas cuentan como duplicados en todos los sistemas, porque Windows los fusionaría.

## A dónde va tu token

Un manifiesto es contenido del repositorio: cualquiera con permiso de escritura, o un PR, podría poner en él la dirección de un
servidor propio y recibir el token que tú tienes para *tu* Vault. Por eso **el manifiesto no decide a dónde van tus credenciales**:

- **Vault**: la `address` del manifiesto solo se acepta si es *loopback* (un servidor de desarrollo local), tiene el mismo origen que
  tu propio `VAULT_ADDR`, o su host está en `ENVSYNC_TRUSTED_HOSTS`. Con solo un `VAULT_TOKEN`, un manifiesto que apunte a otro
  sitio falla con un mensaje que explica cómo confirmarlo. Sin `address` en el manifiesto se usa tu `VAULT_ADDR`.
- **Azure**: solo se ofrece la credencial a hosts de Key Vault o Managed HSM.
- **`ENVSYNC_TRUSTED_HOSTS`**: hosts exactos o `*.dominio`, separados por comas, punto y coma o espacios. Es una variable de **tu**
  entorno, que el repositorio no puede tocar. `*.corp.example.com` cubre subdominios, no `corp.example.com` ni `evilcorp.example.com`.

## Comandos

Opciones comunes: `--manifest`, `--profile` (o `ENVSYNC_PROFILE`), `--timeout <s>` (15, máx. 86400), `--concurrency <n>` (8, máx. 256), `--verbose`.
Un valor **vacío** en `--manifest`, `--profile` o `ENVSYNC_PROFILE` es un error de uso, no un "no indicado": un `STAGE` sin definir en CI
no debe caer en silencio al perfil por defecto. Un perfil elegido por defecto se anuncia en stderr.

### `envsync run -- programa args...`

Lanza el programa con los secretos en **su** entorno y en ningún otro sitio. Hereda stdin/stdout/stderr, así que conserva el
terminal. Su código de salida pasa intacto. Si falta una llave requerida **no se lanza** y se explica qué falta.

**El `--` es obligatorio.** Sin él, `-v`, `-p` y `-m` que el programa lleve después se leerían como opciones de envsync y se quitarían
de sus argumentos sin avisar: `envsync run -- npm --version`.

En Windows, los `.cmd` y `.bat` (`npm.cmd`, `az.cmd`, `mvn.cmd`...) se encuentran gracias a `PATHEXT`, pero cmd.exe **reinterpreta** sus
argumentos con reglas propias: `&` inicia otro comando y `%VAR%` se expande (con los secretos recién inyectados). Un argumento con
`" % & | < > ^ ! ( )` o un salto de línea se **rechaza** para esos destinos; los `.exe` no tienen esa restricción.

`run` hereda tu entorno completo, incluidas las credenciales de los proveedores (`VAULT_TOKEN`, `AZURE_CLIENT_SECRET`,
`AWS_SECRET_ACCESS_KEY`), porque muchos programas necesitan las mismas credenciales de AWS o Azure. Quítalas tú si el hijo no debe verlas.

### `envsync env --shell <pwsh|powershell|bash|zsh|cmd>`

Imprime un script que fija las variables en la sesión actual. Por defecto: PowerShell en Windows, bash en el resto.

```powershell
envsync env --shell pwsh | Invoke-Expression        # PowerShell 7 y Windows PowerShell 5.1
```
```bash
eval "$(envsync env --shell bash)"                  # bash y zsh
```
```bat
for /f "usebackq delims=" %L in (`envsync env --shell cmd`) do %L      :: cmd; en un .cmd escribe %%L
```

`envsync` debe estar en el `PATH`: cmd solo quita un par de comillas exteriores, así que el comando de dentro no puede llevar ninguna.
Es el idioma que **no toca el disco**: `for /f` ejecuta cada línea que imprime envsync, y los secretos pasan de su stdout a la propia consola.

- **Falla en voz alta.** `eval` de una salida vacía tiene éxito, así que si no se puede construir el entorno (llave que falta,
  manifiesto inválido, incluso un error de tecleo en las opciones), lo único que se imprime en stdout es **una sentencia que hace
  fallar a quien la evalúa** con el mismo código de salida: `(exit 11)` en bash y zsh, `throw '...'` en PowerShell, `cmd /c exit 11` en cmd.
  Nunca un script parcial ni un secreto. Con `set -e` el script se detiene; sin él, comprueba `$?`.
  Un shell no soportado no recibe nada, porque no se conoce su sintaxis.
- **Se niega a imprimir en un terminal** (los secretos quedarían en el historial de la pantalla); hay que canalizarlo, o pasar
  `--unsafe-print`. Esa negativa se decide antes de pedir ningún secreto. stdout lleva **solo** el script; todo lo demás va a stderr.
- **PowerShell recibe ASCII puro**: PowerShell decodifica la salida de un comando nativo con la página de códigos de la consola, no con
  UTF-8, y un carácter fuera de ASCII podría cambiar de valor o incluso cortar la cadena. Todo lo que no es ASCII imprimible se
  escribe como `[char]N`.
- **`cmd` es el más restrictivo**: solo ASCII imprimible sin `% " ! ^`, y sentencias de menos de 8000 caracteres (cmd corta la línea en
  8191). Un valor que no encaje se rechaza (código 14) en vez de escaparse "a ojo"; `run` no pasa por un shell y sí puede llevarlo.
- En PowerShell y `cmd`, asignar un valor **vacío** elimina la variable en Windows (por eso un secreto vacío ya es un error).
- Una opcional omitida **no** se quita de tu shell (`env` no puede): el aviso lo dice.

### `envsync check [--offline] [--format text|json]`

Comprueba que están todas las llaves y **nunca imprime valores**. Con `--offline` solo valida el manifiesto y los ajustes de los
proveedores, sin peticiones a los gestores de secretos: el código 0 significa entonces "configuración válida", **no** "las llaves existen"
(el JSON lo dice con `"verified": false`). Ante un fallo, además del informe en stdout escribe un resumen en stderr.

```text
Profile 'dev'
VARIABLE     PROVIDER  STATUS   DETAIL
DB_PASSWORD  kv        OK
JWT_SECRET   vault     MISSING  Secret 'app' was not found under mount 'secret'.
LOG_LEVEL    kv        SKIPPED  optional, left unset: Secret 'log-level' was not found in the vault.
3 variable(s): 1 ready, 1 missing, 0 failed, 1 skipped.
```

## Autenticación

envsync **no guarda credenciales**; usa las de cada gestor:

- **Azure**: `DefaultAzureCredential` (variables `AZURE_*`, identidad administrada, Azure CLI, Visual Studio...). Prueba con `az login`.
- **AWS**: cadena por defecto del SDK (entorno, `~/.aws`, SSO, roles) o un `profile` con nombre, incluidos los de SSO y de rol.
- **Vault**: `VAULT_TOKEN` o el archivo `~/.vault-token` que escribe `vault login`.

## Códigos de salida

| Código | Significado |
|---|---|
| 0 | Todo correcto (en `run`, el código del programa hijo) |
| 10 | Manifiesto o configuración inválidos |
| 11 | Faltan llaves requeridas (no existen o están vacías) |
| 12 | Fallo de autenticación, de red o de un proveedor |
| 13 | Error interno, incluida una excepción inesperada de un proveedor (no es una caída de red: no sirve reintentar) |
| 14 | Un secreto existe, pero el shell pedido no puede llevar su valor de forma segura |
| 64 | Uso incorrecto de la línea de comandos |
| 126 / 127 | El programa de `run` no se pudo iniciar / no se encontró |
| 130 | Cancelado por el usuario |

Los códigos propios de envsync están en 10-14 para no chocar con los que ya usan la mayoría de programas (0, 1, 2).

## Modelo de seguridad y límites

Lo que **sí** se garantiza (con tests):

- Los secretos no se escriben a disco. `SecretValue` se muestra siempre como `[REDACTED]`, así que un log o una excepción no lo filtran.
- Lo que viene de un manifiesto o de un proveedor se **sanea** antes de mostrarse: caracteres de control, saltos de línea, secuencias de
  escape y anulaciones bidireccionales se sustituyen, para que no puedan borrar ni falsificar líneas en un terminal o en un log de CI.
- Los mensajes de error de los proveedores se conservan completos (en una línea y acotados), porque suelen traer justo lo que hay que
  corregir; pueden nombrar recursos (IDs de cuenta, roles), **nunca valores secretos**.
- El token de Vault nunca se sigue en una redirección, no viaja por `http://` (salvo `localhost`) y el manifiesto no elige a dónde va.
- Las respuestas de Vault se limitan a 1 MiB y el manifiesto a 1 MiB.
- Los emisores de shell se verifican contra bash, PowerShell 7, Windows PowerShell 5.1 y cmd **reales**: 33 valores hostiles en bash
  y PowerShell y 11 en cmd (el subconjunto que admite), comprobando en cada uno que el valor vuelve idéntico y que no se ejecuta nada;
  PowerShell además **a través de una tubería nativa real**, con la codificación de consola por defecto.

Lo que **no** puede hacer, y conviene saber:

- Las variables de entorno son visibles para otros procesos **del mismo usuario** (`/proc/<pid>/environ`, depuradores). Es inherente al
  mecanismo; lo que se evita es el archivo en disco, no la exposición al propio usuario.
- Un `string` de .NET no se puede borrar de la memoria. Se limpian los búferes propios (script, transcodificación), pero los valores
  siguen en la memoria de envsync mientras corre `run`: envsync espera al hijo y conserva las referencias.
- **Señales.** Ctrl+C llega también al hijo (mismo grupo de procesos) y envsync espera hasta 10 s a que termine solo antes de pararlo.
  **SIGTERM y SIGHUP no se reenvían al hijo**: al recibirlos, envsync espera esos 10 s y luego lo mata. En un contenedor
  (`docker stop`) el hijo no tiene ocasión de apagarse con gracia. No se ha verificado el comportamiento con señales reales, solo la
  lógica de cancelación con tests.
- El manifiesto se busca hacia arriba hasta la raíz, como git: no ejecutes envsync en un directorio compartido donde otro usuario pueda
  haber dejado un `envsync.json`.
- `--offline` no hace peticiones a los gestores de secretos, pero el SDK de AWS puede leer tus archivos de credenciales locales al
  construirse; no se ha comprobado con una captura de red que no salga nada más.
- Git Bash (Cygwin) descarta un `\r` crudo al leer un script, incluso entre comillas; el emisor de bash lo escribe como `$'\r'`.

## Arquitectura

```text
Domain          records y tipos de valor puros (SecretValue, SecretReference, Profile, Result<T>)
Application     CQRS: consultas y comandos como `readonly record struct`, handlers ValueTask, puertos
Infrastructure  parser del manifiesto, emisores de shell, lanzador de procesos
Providers.*     Azure, AWS y Vault, cada uno en su proyecto (los SDK pesados quedan aislados)
Cli             System.CommandLine y la raíz de composición (DI "a mano", sin contenedor ni reflexión)
```

- **CQRS sin mediador**: `ResolveEnvironmentQuery`, `CheckRequirementsQuery`, `ExportEnvironmentQuery` y `RunProcessCommand` son
  `readonly record struct`; los handlers implementan `IQueryHandler` / `ICommandHandler`. Se pasan por valor porque un método `async`
  no admite parámetros `in`.
- **"Cero asignaciones" con alcance honesto**: se cumple, y se **verifica** con `GC.GetAllocatedBytesForCurrentThread`, en el
  análisis de referencias, la validación de nombres, los tres emisores de shell y el búfer con pool. No se cumple, ni puede, en las
  llamadas de red de los SDK ni al materializar el valor final del secreto como `string`.
- **Resolución concurrente y acotada**: un proveedor por alias, tope de peticiones simultáneas, timeout por secreto y un arreglo
  preasignado donde cada tarea escribe su posición (sin locks, orden estable). Una factoría o un `Dispose` que lance no rompe la ejecución.
- **Preparado para Native AOT**: los analizadores de trimming/AOT están activos en todo `src/` con warnings como errores, y el código
  propio no usa reflexión ni escaneo de ensamblados. **Aún no se ha publicado como AOT**: no se ha verificado que los SDK de Azure y
  AWS lo permitan.

## Desarrollo

```bash
dotnet build envsync.slnx
dotnet test --solution envsync.slnx
```

> **No pases `--nologo` a `dotnet test`.** En el SDK 10 se reenvía al host de xUnit v3, que lo rechaza, y el resultado engañoso es
> "Zero tests ran" con código 5.

- Tests con xUnit v3 sobre Microsoft.Testing.Platform, dobles escritos a mano (sin librerías de mocking) y desarrollo guiado por tests.
- Las pruebas contra shells reales se **omiten**, no se simulan, si el shell no está instalado en la máquina.
- `tests/EnvSync.Cli.Tests/EndToEndTests.cs` ejecuta el binario real contra un Vault simulado en loopback y un proceso hijo real, e incluye
  el idioma `for /f` de cmd contra un `cmd.exe` real.
- Estos tests solo se han ejecutado en Windows. La matriz de `.github/workflows/ci.yml` está pensada para ejecutarlos en Linux y macOS.
