﻿using Api.Models;
using AutoMapper;
using Api.Configs;
using AutoMapper.QueryableExtensions;
using Common;
using DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using DAL.Entities;

namespace Api.Services
{
    public class UserService : IDisposable
    {

        private readonly IMapper _mapper;
        private readonly DAL.DataContext _context;
        private readonly AuthConfig _config;

        public UserService(IMapper mapper, DataContext context, IOptions<AuthConfig> config)
        {
            _mapper = mapper;
            _context = context;
            _config = config.Value;
        }
        public async Task<Guid> CreateUser(CreateUserModel model)
        {
            var dbuser = _mapper.Map<DAL.Entities.User>(model);
            var t = await _context.Users.AddAsync(dbuser);
            await _context.SaveChangesAsync();
            return t.Entity.Id;
        }
        public async Task<List<UserModel>> GetUsers()
        {
            return await _context.Users.AsNoTracking().ProjectTo<UserModel>(_mapper.ConfigurationProvider).ToListAsync();
        }
        public async Task<bool> CheckUserExist(string email)
        {

            return await _context.Users.AnyAsync(x => x.Email.ToLower() == email.ToLower());

        }
        public async Task Delete(Guid id)
        {
            var dbUser = await GetUserById(id);
            if (dbUser != null)
            {
                _context.Users.Remove(dbUser);
                await _context.SaveChangesAsync();
            }
        }
        private async Task<DAL.Entities.User> GetUserByCredention(string login, string pass)
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Email.ToLower() == login.ToLower());
            if (user == null)
                throw new Exception("user not found");

            if (!HashHelper.Verify(pass, user.PasswordHash))
                throw new Exception("password is incorrect");

            return user;
        }

        private async Task<DAL.Entities.User> GetUserById(Guid id)
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (user == null)
                throw new Exception("user not found");
            return user;
        }
        public async Task<UserModel> GetUser(Guid id)
        {
            var user = await GetUserById(id);

            return _mapper.Map<UserModel>(user);

        }
        private TokenModel GenerateTokens(DAL.Entities.UserSession session)
        {
            var dtNow = DateTime.Now;
            if (session.User == null)
                throw new Exception("impossible");

            var jwt = new JwtSecurityToken(
                issuer: _config.Issuer,
                audience: _config.Audience,
                notBefore: dtNow,
                claims: new Claim[]
            {
                new Claim(ClaimsIdentity.DefaultNameClaimType, session.User.Name),
                new Claim("sessionId", session.Id.ToString()),
                new Claim("id",session.User.Id.ToString()),
            },
                expires: DateTime.Now.AddMinutes(_config.LifeTime),
                signingCredentials: new SigningCredentials(_config.SymmetricSecurityKey(), SecurityAlgorithms.HmacSha256)
                );

            var encodedJwt = new JwtSecurityTokenHandler().WriteToken(jwt);

            var refresh = new JwtSecurityToken(
               notBefore: dtNow,
               claims: new Claim[]{
                new Claim("refreshToken",session.RefreshToken.ToString()),
                },
               expires: DateTime.Now.AddMinutes(_config.LifeTime),
               signingCredentials: new SigningCredentials(_config.SymmetricSecurityKey(), SecurityAlgorithms.HmacSha256)
               );

            var encodedRefresh = new JwtSecurityTokenHandler().WriteToken(refresh);

            return new TokenModel(encodedJwt, encodedRefresh);
        }

        public async Task<TokenModel> GetToken (string login, string password)
        {
            var user = await GetUserByCredention(login, password);
            var session = await _context.UserSessions.AddAsync(new DAL.Entities.UserSession
            {
                User = user,
                RefreshToken = Guid.NewGuid(),
                Created = DateTime.UtcNow,
                Id = Guid.NewGuid()
            });
            await _context.SaveChangesAsync();
            return GenerateTokens(session.Entity);
        }
        public async Task<UserSession> GetSessionById(Guid id)
        {
            var session = await _context.UserSessions.FirstOrDefaultAsync(x => x.Id == id);
            if (session == null)
            {
                throw new Exception("session is not found");
            }
            return session;
        }
        private async Task<UserSession> GetSessionByRefreshToken(Guid id)
        {
            var session = await _context.UserSessions.Include(x => x.User).FirstOrDefaultAsync(x => x.RefreshToken == id);
            if (session == null)
            {
                throw new Exception("session is not found");
            }
            return session;
        }

        public async Task<TokenModel> GetTokenByRefreshToken(string refreshToken)
        {
            var validParams = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                IssuerSigningKey = _config.SymmetricSecurityKey()
            };
            var principal = new JwtSecurityTokenHandler().ValidateToken(refreshToken, validParams, out var securityToken);

            if (securityToken is not JwtSecurityToken jwtToken
                || !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256,
                StringComparison.InvariantCultureIgnoreCase))
            {
                throw new SecurityTokenException("invalid token");
            }

            if (principal.Claims.FirstOrDefault(x => x.Type == "refreshToken")?.Value is String refreshIdString
                && Guid.TryParse(refreshIdString, out var refreshId)
                )
            {
                var session = await GetSessionByRefreshToken(refreshId);
                if (!session.IsActive)
                {
                    throw new Exception("session is not active");
                }

                session.RefreshToken = Guid.NewGuid();
                await _context.SaveChangesAsync();

                return GenerateTokens(session);
            }
            else
            {
                throw new SecurityTokenException("invalid token");
            }
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
