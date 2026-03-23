module SampleWeb.Handlers

open SampleLib.Math

/// Handler for GET /api/math/{a}/{b}/add
let handleAdd a b = add a b

/// Handler for GET /api/math/{a}/{b}/multiply
let handleMultiply a b = multiply a b
