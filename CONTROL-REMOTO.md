# A&T Printer Control — Control remoto del agente

Desde la pestaña **Control remoto**, el administrador elige un agente activo y puede:
- activar/desactivar la búsqueda por USB;
- activar/desactivar la búsqueda de red;
- agregar/quitar una IP individual;
- agregar/quitar rangos IPv4;
- seleccionar el intervalo de sincronización (30 minutos, 1 hora o 2 horas);
- enviar la orden **Buscar ahora**.

Cada acción se registra como una orden asociada al proyecto y a la instalación del agente. El agente consulta la cola cada 30 segundos, persiste la configuración en `%ProgramData%\ATPrinterAgent\config.json`, ejecuta el escaneo cuando corresponde y confirma el resultado en el panel. Si la PC está apagada, la orden queda pendiente hasta que vuelva a conectarse.

Los cambios de configuración remota requieren un agente con soporte de comandos remotos y un token por instalación. No compartas claves administrativas de Supabase con el agente o el navegador.
