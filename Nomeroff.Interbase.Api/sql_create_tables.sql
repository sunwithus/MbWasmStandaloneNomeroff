-- Таблицы для Nomeroff (Interbase 2009)
-- Выполните при необходимости создания таблиц.

-- SPR_SPEECH_TABLE должна уже существовать в вашей БД (из Estimator).
-- Для Nomeroff используются поля: S_INCKEY, S_DEVICEID, S_DATETIME, S_NOTICE (и минимальные S_TYPE, S_PRELOOKED).

-- SPR_SP_GEO_TABLE — геоданные сеанса (GPS)
CREATE TABLE SPR_SP_GEO_TABLE (
  S_INCKEY BIGINT NOT NULL PRIMARY KEY,
  S_LATITUDE DOUBLE PRECISION,
  S_LONGITUDE DOUBLE PRECISION
);

-- SPR_SP_FOTO_TABLE — фотоснимки сеанса (скриншот при распознавании)
CREATE TABLE SPR_SP_FOTO_TABLE (
  S_INCKEY BIGINT NOT NULL PRIMARY KEY,
  F_IMAGE BLOB
);
