using Microsoft.OpenApi.Models;
using ReplicationDemo.Application;
using ReplicationDemo.Application.Consistency;
using ReplicationDemo.DAL;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "ReplicationDemo API", Version = "v1" });
    c.AddSecurityDefinition("UserIdHeader", new OpenApiSecurityScheme
    {
        Name = "X-User-Id",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "User identifier for ReadAfterWrite consistency tracking"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "UserIdHeader" }
            },
            []
        }
    });
});

// Bind consistency settings from configuration ("Consistency" section in appsettings.json)
builder.Services.Configure<ConsistencySettings>(
    builder.Configuration.GetSection(ConsistencySettings.SectionName));

builder.Services.AddDataAccess(builder.Configuration);
builder.Services.AddApplication();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();
