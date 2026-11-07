using KeySwitch.BuildAssets;

// Точка входа генератора ассетов. Вся логика — в BuildProgram, чтобы файл оставался читаемым.
return await BuildProgram.RunAsync(args).ConfigureAwait(false);
