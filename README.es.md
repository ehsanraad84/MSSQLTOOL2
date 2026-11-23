# MSSQL Tool para SSMS 22

[English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | **Español**

[![Install from VSIX Gallery](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

🔥 **Más de 100 descargas y sumando — ¡gracias por el apoyo!**

## Enlaces

Repositorio de GitHub

https://github.com/PuLuShen/MSSQLTOOL

Última versión (ZIP + VSIX)

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS Gallery (para SQL Server Management Studio)

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery (para Visual Studio)

https://www.vsixgallery.com/extension/MSSQLTool

Documentación (Wiki)

https://github.com/PuLuShen/MSSQLTOOL/wiki

## Instalación

1. Descargue el ZIP de la última versión indicada arriba y ejecute `安装或重装.cmd` (recomendado).
2. O instale el VSIX más reciente desde SSMS Gallery (arriba).
3. Las actualizaciones están integradas: **Configuración → Actualizaciones** puede programar «instalar al cerrar SSMS».

> **MSSQL Tool es una extensión gratuita y de código abierto para SSMS (SQL Server Management Studio) 22** —
> un complemento para SQL Server Management Studio que añade autocompletado de SQL sensible al contexto, formato T-SQL,
> historial de consultas, supervisión del estado de los servidores y exportación de resultados a Excel/Google Sheets/correo.
>
> Los usuarios de SSMS 22 (SQL Server Management Studio versión 22) suelen buscar complementos gratuitos que sustituyan
> a SQL Prompt; MSSQL Tool está pensado exactamente para eso: una extensión de SSMS gratuita con autocompletado de SQL,
> formato y procesamiento de resultados integrados, para desarrolladores de SQL Server y DBA que quieren escribir
> consultas más rápido y procesar más resultados dentro de SQL Server Management Studio.

> MSSQL Tool es un fork de [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools) (Apache-2.0). El motor de autocompletado de SQL, las opciones de formato y las capas de configuración y localización se rehicieron sobre esa base.

## Aspectos destacados

- **Interfaz multilingüe (8 idiomas: English / 简体中文 / 繁體中文 / 日本語 / 한국어 / Deutsch / Français / Español)** — la mayoría de las extensiones de SSMS solo están en inglés.
- **Alternativa gratuita a SQL Prompt** para autocompletado de SQL, formato T-SQL, historial de consultas y exportación de resultados a Excel/correo.
- **Basada en Axial SQL Tools** (mantenida desde 2016), con un motor de autocompletado y una capa de localización mejorados.

*Nota: la exportación a Google Sheets y la sincronización de scripts de GitHub son experimentales y están en desarrollo activo.*

## Capturas de pantalla

**Autocompletado inteligente de SQL** — sugerencias sensibles al contexto mientras escribe: esquemas, tablas, vistas, columnas, procedimientos, funciones y parámetros, cada uno con un icono de tipo, un filtro de categoría y los detalles del objeto resaltado.

<img src="pics/1.png" width="620" alt="Lista de autocompletado de SQL al escribir SELECT * FROM">

**Configuración** — cada función tiene sus propias opciones y se guardan al instante. La interfaz sigue el idioma de SSMS (aquí se muestra chino simplificado; también está disponible el inglés y otros idiomas).

<img src="pics/2.png" width="760" alt="Ventana de configuración de MSSQL Tool con la pestaña de autocompletado de SQL">

**Menú Herramientas** — los comandos de la extensión, con su atajo de teclado cuando está configurado.

<img src="pics/3.png" width="520" alt="Comandos de MSSQL Tool en el menú Herramientas de SSMS">

**Estadísticas del conjunto de resultados** — seleccione celdas en la cuadrícula de resultados para ver recuento, suma, media, mínimo, máximo y valores distintos; el mismo panel puede localizar una columna, buscar un valor y fijar columnas.

<img src="pics/4.png" width="820" alt="Panel de estadísticas de MSSQL Tool junto a una cuadrícula de resultados">

## Funciones

### Edición de SQL

- **Autocompletado inteligente de SQL:** sugerencias sensibles al contexto para bases de datos, esquemas, tablas, vistas, columnas, procedimientos, funciones, parámetros, sinónimos y objetos de servidores vinculados.
- **Un autocompletado que sigue sus alias:** `c.` lista solo las columnas de esa tabla e inserta el nombre de columna desnudo, de modo que el alias que escribió queda delante.
- **Compatibilidad con UPDATE:** `UPDATE t SET` sugiere las columnas escribibles del destino, incluso cuando el destino es un alias declarado más abajo en la cláusula `FROM`.
- **Tipos de datos y sugerencias:** `INT`, `NVARCHAR(50)`, `DATETIME2` y las formas habituales `WITH (NOLOCK)` / `OPTION (RECOMPILE)` se ofrecen donde corresponde.
- **Ayuda de funciones en la lista:** más de 36 funciones integradas muestran su firma, una explicación de una línea y un ejemplo listo para copiar.
- **Palabras clave ampliables:** la lista integrada puede ampliarse desde Configuración → Autocompletado de SQL (una palabra clave personalizada por línea).
- **Caché de metadatos del autocompletado:** carga en segundo plano con progreso paso a paso, borrado/actualización manual y confianza opcional en el certificado de SQL Server.
- **F12 Ir a la definición:** abre definiciones de objetos de la base de datos y navega a definiciones locales de SQL.
- **Formato T-SQL** (se conserva para los hábitos existentes; SSMS 22 ya formatea de forma nativa — véase *Formato* más abajo)
- **Plantillas de consulta:** edición en línea con Ctrl+S, favoritos, **etiquetas** (`-- tags: 报表, 日结`) con búsqueda por etiqueta (`#tag`) y una lista de los **10 recientes** en la parte superior del selector.
- **Fragmentos de código**
- **Expandir `SELECT *` a una lista de columnas**
- **Seleccionar la instrucción actual**
- **Alternar comentario de bloque**
- **Búsqueda rápida:** búsqueda de objetos en varias bases de datos, con el destino tomado del Explorador de objetos, filtros, generación de scripts y apertura en una consulta nueva.
- **Script de definición de objeto**

### Herramientas del conjunto de resultados

- **Panel de estadísticas (flotante):** se abre solo al hacer clic en los resultados y se cierra al hacer clic fuera. Seleccione celdas y muestra el **recuento, la suma, la media, el mínimo, el máximo y los valores distintos** de esa selección, además del nombre real de la columna; se puede redimensionar, recuerda su tamaño y posición y puede **fijar las primeras N columnas**.
- **Búsqueda de columnas:** escriba parte de un nombre de columna y los resultados se desplazan hasta ella (`DDZ` nunca se resuelve como `DDZT`).
- **Ventana de herramientas del conjunto de resultados:** la misma búsqueda más estadísticas por columna en un diálogo acoplado (Herramientas → *Búsqueda y estadísticas del conjunto de resultados*), con copia al portapapeles.

### Procesamiento de resultados y transferencia de datos

- **Exportar la cuadrícula a Excel**
- **Enviar los resultados de la cuadrícula por correo**
- **Exportar la cuadrícula como tabla temporal**
- **Copiar nombres de columna y valores como INSERT, CSV, JSON, XML o HTML**
- **Transferencia masiva de datos**

### Supervisión y flujo de trabajo

- **Historial de consultas**
- **Panel de estado del servidor**
- **Resumen de estadísticas de ejecución**
- **Avisos de transacciones y Always Encrypted**
- **Colores de conexión por entorno de servidor y base de datos**
- **Tiempo de ejecución de consultas en milisegundos**
- **Valores numéricos alineados a la derecha en la cuadrícula**
- **Información de la versión de SQL Server**
- **Carpeta de datos y configuración personalizable** (otra unidad, una carpeta sincronizada o un dispositivo portátil)

## Funciones experimentales

Funcionan, pero su diseño aún está cambiando: espere cambios entre versiones y revise de nuevo los ajustes relacionados tras actualizar.

| Función | Estado |
| --- | --- |
| **Exportación a Google Sheets** | Beta — la configuración de OAuth y la asignación de columnas siguen cambiando. |
| **Sincronización de scripts de GitHub** | Beta — envío y descarga de archivos completos, resolución de conflictos manual. |

## Formato

SSMS 22 incluye su propio formateador de T-SQL, por lo que **la función de formato de esta extensión ya no se desarrolla**. El comando sigue funcionando y se mantiene por costumbre — ahora está en el menú **Herramientas** (Ctrl+Shift+Alt+F) — pero no se añadirán nuevas opciones de formato.

## Apoyo

<img src="pics/赞赏码.png" width="320" alt="Código QR de donación" />

## Preguntas frecuentes

**¿Qué es MSSQL Tool?**
Una extensión gratuita para SSMS 22 de autocompletado de SQL, formato y procesamiento de resultados.

**¿MSSQL Tool es gratuito?**
Sí, es de código abierto bajo Apache-2.0.

**¿Cómo se compara con SQL Prompt o dbForge?**
MSSQL Tool es gratuito y de código abierto, con sincronización de GitHub y exportación a Google Sheets integradas. SQL Prompt y dbForge son alternativas comerciales.
