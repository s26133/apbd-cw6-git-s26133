using ClinicApi.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")!;
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var appointments = new List<AppointmentListDto>();
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, 
                   p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """, connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value = (object?)patientLastName ?? DBNull.Value;

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }

        return Ok(appointments);
    }

    [HttpGet("{idAppointment}")]
    public async Task<IActionResult> GetAppointmentDetails(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                   p.Email, p.PhoneNumber, d.LicenseNumber
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
            WHERE a.IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        }

        var dto = new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(0),
            AppointmentDate = reader.GetDateTime(1),
            Status = reader.GetString(2),
            Reason = reader.GetString(3),
            InternalNotes = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            PatientEmail = reader.GetString(6),
            PatientPhoneNumber = reader.GetString(7),
            DoctorLicenseNumber = reader.GetString(8)
        };

        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate <= DateTime.Now)
            return BadRequest(new ErrorResponseDto { Message = "Appointment date must be in the future." });

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto { Message = "Reason must not be empty and max 250 characters." });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        if (!await CheckIfUserActive(connection, "Patients", "IdPatient", request.IdPatient))
            return BadRequest(new ErrorResponseDto { Message = "Patient does not exist or is inactive." });

        if (!await CheckIfUserActive(connection, "Doctors", "IdDoctor", request.IdDoctor))
            return BadRequest(new ErrorResponseDto { Message = "Doctor does not exist or is inactive." });

        if (await CheckDoctorConflict(connection, request.IdDoctor, request.AppointmentDate, null))
            return Conflict(new ErrorResponseDto { Message = "Doctor already has an appointment at this time." });

        await using var command = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, 'Scheduled', @Reason);
            """, connection);

        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;

        var newId = Convert.ToInt32(await command.ExecuteScalarAsync());
        return CreatedAtAction(nameof(GetAppointmentDetails), new { idAppointment = newId }, null);
    }

    [HttpPut("{idAppointment}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto request)
    {
        var validStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
        if (!validStatuses.Contains(request.Status))
            return BadRequest(new ErrorResponseDto { Message = "Invalid status." });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var getCommand = new SqlCommand("SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @IdAppointment", connection);
        getCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await getCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });

        var currentStatus = reader.GetString(0);
        var currentDate = reader.GetDateTime(1);
        await reader.CloseAsync();

        if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
            return BadRequest(new ErrorResponseDto { Message = "Cannot change date of a completed appointment." });

        if (!await CheckIfUserActive(connection, "Patients", "IdPatient", request.IdPatient))
            return BadRequest(new ErrorResponseDto { Message = "Patient does not exist or is inactive." });

        if (!await CheckIfUserActive(connection, "Doctors", "IdDoctor", request.IdDoctor))
            return BadRequest(new ErrorResponseDto { Message = "Doctor does not exist or is inactive." });

        if (currentDate != request.AppointmentDate && await CheckDoctorConflict(connection, request.IdDoctor, request.AppointmentDate, idAppointment))
            return Conflict(new ErrorResponseDto { Message = "Doctor already has an appointment at this time." });

        await using var updateCommand = new SqlCommand("""
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient, IdDoctor = @IdDoctor, AppointmentDate = @AppointmentDate,
                Status = @Status, Reason = @Reason, InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """, connection);

        updateCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        updateCommand.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        updateCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        updateCommand.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        updateCommand.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = request.Status;
        updateCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;
        updateCommand.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value = (object?)request.InternalNotes ?? DBNull.Value;

        await updateCommand.ExecuteNonQueryAsync();
        return Ok();
    }

    [HttpDelete("{idAppointment}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var checkCommand = new SqlCommand("SELECT Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment", connection);
        checkCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        var status = (string?)await checkCommand.ExecuteScalarAsync();
        if (status == null)
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });

        if (status == "Completed")
            return Conflict(new ErrorResponseDto { Message = "Cannot delete a completed appointment." });

        await using var deleteCommand = new SqlCommand("DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment", connection);
        deleteCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await deleteCommand.ExecuteNonQueryAsync();

        return NoContent();
    }

    private async Task<bool> CheckIfUserActive(SqlConnection connection, string table, string idColumn, int id)
    {
        var query = $"SELECT IsActive FROM dbo.{table} WHERE {idColumn} = @Id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        var result = await command.ExecuteScalarAsync();
        return result != null && (bool)result;
    }

    private async Task<bool> CheckDoctorConflict(SqlConnection connection, int idDoctor, DateTime date, int? excludeAppointmentId)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.Appointments 
            WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate AND (@ExcludeId IS NULL OR IdAppointment != @ExcludeId)
            """, connection);

        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = date;
        command.Parameters.Add("@ExcludeId", SqlDbType.Int).Value = (object?)excludeAppointmentId ?? DBNull.Value;

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        return count > 0;
    }
}